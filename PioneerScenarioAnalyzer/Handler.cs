using EventLoggerPlugin;
using Gallop;
using UmamusumeResponseAnalyzer.TerminalGui;
using static PioneerScenarioAnalyzer.i18n.Game;

namespace PioneerScenarioAnalyzer
{
    internal static class Handler
    {
        public static int GetCommandInfoStage(SingleModePioneerCheckEventResponse @event)
        {
            //if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0)) return;
            if (@event.data.chara_info.playing_state == 1 && (@event.data.unchecked_event_array == null || @event.data.unchecked_event_array.Length == 0))
            {
                return 2;
            } //常规训练
            else if (@event.data.chara_info.playing_state == 5 && @event.data.unchecked_event_array.Any(x => x.story_id == 400010112)) //选buff
            {
                return 5;
            }
            else if (@event.data.chara_info.playing_state == 5 &&
                (@event.data.unchecked_event_array.Any(x => x.story_id == 830241003))) //选团卡事件
            {
                return 3;
            }
            else
            {
                return 0;
            }
        }
        public static WorkspaceContent ParsePioneerCommandInfo(SingleModePioneerCheckEventResponse @event, int stage)
        {
            var critInfos = new List<string>();
            var turn = new TurnInfoPioneer(SingleModeTurnData.From(@event.data));
            var dataset = @event.data.pioneer_data_set;
            var eventLoggerSnapshot = new EventLoggerSnapshot(
                @event.data.chara_info,
                @event.data.unchecked_event_array,
                @event.data.select_index_info_array);
            var round = EventLogger.Current;

            if (round.CurrentTurn != turn.Turn - 1 //正常情况
                && round.CurrentTurn != turn.Turn //重复显示
                && turn.Turn != 1 //第一个回合
                )
            {
                critInfos.Add(string.Format(I18N_WrongTurnAlert, round.CurrentTurn, turn.Turn));
                EventLogger.ResetSession(eventLoggerSnapshot, isFullGame: false);
                round = EventLogger.Current;
            }
            else if (turn.Turn == 1)
            {
                EventLogger.ResetSession(eventLoggerSnapshot, isFullGame: true);
                round = EventLogger.Current;
            }

            //买技能，大师杯剧本年末比赛，会重复显示
            if (@event.data.chara_info.playing_state != 1)
            {
                critInfos.Add(I18N_RepeatTurn);
            }
            else
            {
                EventLogger.BeginScenarioTurn(
                    eventLoggerSnapshot,
                    @event.data.chara_info.scenario_id,
                    turn.Turn);
                round = EventLogger.Current;
            }
            var trainItems = new Dictionary<int, SingleModeCommandInfo>
            {
                { 101, @event.data.home_info.command_info_array[0] },
                { 105, @event.data.home_info.command_info_array[1] },
                { 102, @event.data.home_info.command_info_array[2] },
                { 103, @event.data.home_info.command_info_array[3] },
                { 106, @event.data.home_info.command_info_array[4] }
            };
            var trainStats = new TrainStats[5];
            var turnStat = @event.data.chara_info.playing_state != 1
                ? new TurnStats()
                : round.NewTurnBuilder(turn.Turn);
            turnStat.motivation = @event.data.chara_info.motivation;
            var failureRate = new Dictionary<int, int>();

            var totalValue = turn.StatsRevised.Sum();

            for (var i = 0; i < 5; i++)
            {
                var trainId = TurnInfoPioneer.TrainIds[i];
                failureRate[trainId] = trainItems[trainId].failure_rate;
                var trainParams = new Dictionary<int, int>()
                {
                    {1,0},
                    {2,0},
                    {3,0},
                    {4,0},
                    {5,0},
                    {30,0},
                    {10,0},
                };
                foreach (var item in turn.GetCommonResponse().home_info.command_info_array)
                {
                    if (TurnInfoPioneer.ToTrainId.TryGetValue(item.command_id, out var value) && value == trainId)
                    {
                        foreach (var trainParam in item.params_inc_dec_info_array)
                            trainParams[trainParam.target_type] += trainParam.value;
                    }
                }

                var stats = new TrainStats
                {
                    FailureRate = trainItems[trainId].failure_rate,
                    VitalGain = trainParams[10]
                };
                if (turn.Vital + stats.VitalGain > turn.MaxVital)
                    stats.VitalGain = turn.MaxVital - turn.Vital;
                if (stats.VitalGain < -turn.Vital)
                    stats.VitalGain = -turn.Vital;
                stats.FiveValueGain = [trainParams[1], trainParams[2], trainParams[3], trainParams[4], trainParams[5]];
                stats.PtGain = trainParams[30];

                var valueGainUpper = dataset.command_info_array.FirstOrDefault(x => x.command_id == trainId || x.command_id == TurnInfoPioneer.XiahesuIds[trainId])?.params_inc_dec_info_array;
                if (valueGainUpper != null)
                {
                    foreach (var item in valueGainUpper)
                    {
                        if (item.target_type == 30)
                            stats.PtGain += item.value;
                        else if (item.target_type <= 5)
                            stats.FiveValueGain[item.target_type - 1] += item.value;
                    }
                }

                for (var j = 0; j < 5; j++)
                    stats.FiveValueGain[j] = ScoreUtils.ReviseOver1200(turn.Stats[j] + stats.FiveValueGain[j]) - ScoreUtils.ReviseOver1200(turn.Stats[j]);

                if (turn.Turn == 1)
                {
                    turnStat.trainLevel[i] = 1;
                    turnStat.trainLevelCount[i] = 0;
                }
                else
                {
                    var previousTurn = round.Turns[turn.Turn - 1];
                    var lastTrainLevel = previousTurn?.TrainLevel[i] ?? 1;
                    var lastTrainLevelCount = previousTurn?.TrainLevelCount[i] ?? 0;

                    turnStat.trainLevel[i] = lastTrainLevel;
                    turnStat.trainLevelCount[i] = lastTrainLevelCount;
                    if (previousTurn is not null &&
                        previousTurn.PlayerChoice == TurnInfoPioneer.TrainIds[i] &&
                        !previousTurn.IsTrainingFailed &&
                        !((turn.Turn - 1 >= 37 && turn.Turn - 1 <= 40) || (turn.Turn - 1 >= 61 && turn.Turn - 1 <= 64))
                        )//上回合点的这个训练，计数+1
                        turnStat.trainLevelCount[i] += 1;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }
                    //检查是否有剧本全体训练等级+1
                    if (turn.Turn == 25 || turn.Turn == 37 || turn.Turn == 49)
                        turnStat.trainLevelCount[i] += 4;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }

                    if (turnStat.trainLevel[i] >= 5)
                    {
                        turnStat.trainLevel[i] = 5;
                        turnStat.trainLevelCount[i] = 0;
                    }

                    var trainlv = @event.data.chara_info.training_level_info_array.First(x => x.command_id == TurnInfoPioneer.TrainIds[i]).level;
                    if (turnStat.trainLevel[i] != trainlv && stage == 2)
                    {
                        //可能是半途开启小黑板，也可能是有未知bug
                        critInfos.Add($"警告：训练等级预测错误，预测{TurnInfoPioneer.TrainIds[i]}为lv{turnStat.trainLevel[i]}(+{turnStat.trainLevelCount[i]})，实际为lv{trainlv}");
                        turnStat.trainLevel[i] = trainlv;
                        turnStat.trainLevelCount[i] = 0;//如果是半途开启小黑板，则会在下一次升级时变成正确的计数
                    }
                }

                trainStats[i] = stats;
            }
            if (stage == 2)
            {
                // 把训练等级信息更新到GameStats
                turnStat.fiveTrainStats = trainStats;
                EventLogger.CommitScenarioTurn(@event.data.chara_info.scenario_id, turn.Turn, turnStat);
            }

            var baseIslandTrainCommand = turn.GetCommonResponse().home_info.command_info_array.FirstOrDefault(x => x.command_id == 3101);
            var scenarioIslandTrainCommand = turn.GetCommonResponse().pioneer_data_set.command_info_array.FirstOrDefault(x => x.command_id == 3101);
            var baseIslandTrainStats = baseIslandTrainCommand?.params_inc_dec_info_array.Where(x => x.target_type != 10 && x.target_type != 30).Sum(x => x.value);
            var scenarioIslandTrainStats = scenarioIslandTrainCommand?.params_inc_dec_info_array.Where(x => x.target_type != 10 && x.target_type != 30).Sum(x => x.value);
            var baseIslandTrainPt = baseIslandTrainCommand?.params_inc_dec_info_array.FirstOrDefault(x => x.target_type == 30)?.value;
            var scenarioIslandTrainPt = scenarioIslandTrainCommand?.params_inc_dec_info_array.FirstOrDefault(x => x.target_type == 30)?.value;
            var totalIslandTrainStats = baseIslandTrainStats + scenarioIslandTrainStats;
            var totalIslandTrainPt = baseIslandTrainPt + scenarioIslandTrainPt;
            var canTrainOnIsland = baseIslandTrainCommand is not null && scenarioIslandTrainCommand is not null;
            var islandStats = canTrainOnIsland
                ? $"岛训练数值: 属性 {totalIslandTrainStats} | Pt {totalIslandTrainPt}"
                : "岛训练数值: 无法上岛";
            var islandPartners = "岛训练人数: 无法上岛";
            if (baseIslandTrainCommand is not null && scenarioIslandTrainCommand is not null)
            {
                var supportCount = baseIslandTrainCommand.training_partner_array.Where(x => x <= 6).Count();
                var npcCount = baseIslandTrainCommand.training_partner_array.Where(x => x > 6).Count();
                islandPartners = $"岛训练人数: 支援卡 {supportCount} 人 | NPC {npcCount} 人";
            }

            var motivation = @event.data.chara_info.motivation switch
            {
                5 => I18N_MotivationBest,
                4 => I18N_MotivationGood,
                3 => I18N_MotivationNormal,
                2 => I18N_MotivationBad,
                1 => I18N_MotivationWorst,
                _ => throw new InvalidDataException($"未知干劲值: {@event.data.chara_info.motivation}")
            };

            var availableTrainingCount = @event.data.home_info.command_info_array.Count(x => x.is_enable == 1);
            if (availableTrainingCount <= 1)
                critInfos.Add("非训练回合");

            var lines = new List<string>
            {
                $"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth} | 总属性: {totalValue} | {I18N_Vital}: {turn.Vital}/{turn.MaxVital} | {motivation}"
            };
            AppendSection(lines, "重要信息", critInfos);
            AppendSection(lines, "剧本信息",
            [
                $"评价会回合周期 {(turn.Turn - 1) % 6 + 1}/6",
                islandStats,
                islandPartners
            ]);

            lines.Add(string.Empty);
            lines.Add("== 训练信息 ==");
            if (stage == 2)
            {
                var bestScore = trainStats.Max(x => x.FiveValueGain.Sum());
                foreach (var command in turn.CommandInfoArray)
                {
                    var failure = failureRate[TurnInfoPioneer.TrainIds[command.TrainIndex - 1]];
                    var title = command.TrainIndex switch
                    {
                        1 => I18N_Speed,
                        2 => I18N_Stamina,
                        3 => I18N_Power,
                        4 => I18N_Nuts,
                        5 => I18N_Wiz,
                        _ => throw new InvalidDataException($"未知训练索引: {command.TrainIndex}")
                    };
                    lines.Add($"[{title}{(failure > 0 ? $" ({failure}%)" : string.Empty)}]");

                    var currentStat = turn.StatsRevised[command.TrainIndex - 1];
                    var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
                    lines.Add($"  {I18N_CurrentRemainStat}: {currentStat}:{statUpToMax}");

                    var afterVital = trainStats[command.TrainIndex - 1].VitalGain + turn.Vital;
                    turn.PointGainInfoDictionary.TryGetValue(command.CommandId, out var gain);
                    lines.Add($"  {I18N_Vital}: {afterVital}/{turn.MaxVital} | Lv{command.TrainLevel} | {gain}");

                    var stats = trainStats[command.TrainIndex - 1];
                    var score = stats.FiveValueGain.Sum();
                    lines.Add($"  {(score == bestScore ? "▶ " : string.Empty)}{I18N_StatSimple}: {score} | Pt: {stats.PtGain}");

                    foreach (var trainingPartner in command.TrainingPartners)
                        lines.Add($"  {trainingPartner.Name}");
                    lines.Add(string.Empty);
                }
            }
            else
            {
                lines.Add($"非训练阶段，stage={stage}");
            }

            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            AppendSection(lines, "Extras", eventPerf);
            return WorkspaceContent.Text(string.Join(Environment.NewLine, lines));
        }

        static void AppendSection(List<string> output, string title, IEnumerable<string> rows)
        {
            var values = rows.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (values.Length == 0)
                return;

            output.Add(string.Empty);
            output.Add($"== {title} ==");
            output.AddRange(values);
        }
    }
}
