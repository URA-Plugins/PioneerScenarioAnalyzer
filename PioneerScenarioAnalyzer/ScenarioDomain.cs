using System.Collections.Frozen;
using Gallop;
using UmamusumeResponseAnalyzer;

namespace PioneerScenarioAnalyzer;

internal sealed class SingleModeTurnData(
    SingleModeChara charaInfo,
    SingleModeHomeInfo homeInfo,
    SingleModePioneerDataSet pioneerDataSet)
{
    public SingleModeChara chara_info { get; } = charaInfo;
    public SingleModeHomeInfo home_info { get; } = homeInfo;
    public SingleModePioneerDataSet pioneer_data_set { get; } = pioneerDataSet;

    public static SingleModeTurnData From(SingleModePioneerCheckEventResponse.CommonResponse response)
        => new(response.chara_info, response.home_info, response.pioneer_data_set);
}

internal class TurnInfo(SingleModeTurnData resp)
{
    SingleModeChara CharaInfo => resp.chara_info;

    public int SpeedRevised => ReviseOver1200(CharaInfo.speed);
    public int StaminaRevised => ReviseOver1200(CharaInfo.stamina);
    public int PowerRevised => ReviseOver1200(CharaInfo.power);
    public int GutsRevised => ReviseOver1200(CharaInfo.guts);
    public int WizRevised => ReviseOver1200(CharaInfo.wiz);
    public int[] Stats => [CharaInfo.speed, CharaInfo.stamina, CharaInfo.power, CharaInfo.guts, CharaInfo.wiz];
    public int[] StatsRevised => [SpeedRevised, StaminaRevised, PowerRevised, GutsRevised, WizRevised];
    public int[] MaxStatsRevised =>
    [
        ReviseOver1200(CharaInfo.max_speed),
        ReviseOver1200(CharaInfo.max_stamina),
        ReviseOver1200(CharaInfo.max_power),
        ReviseOver1200(CharaInfo.max_guts),
        ReviseOver1200(CharaInfo.max_wiz)
    ];
    public int Turn => CharaInfo.turn;
    public int Year => (Turn - 1) / 24 + 1;
    public int Vital => CharaInfo.vital;
    public int MaxVital => CharaInfo.max_vital;
    public FrozenDictionary<int, int> SupportCards => CharaInfo.support_card_array.ToDictionary(x => x.position, x => x.support_card_id).ToFrozenDictionary();
    public FrozenDictionary<int, EvaluationInfo> Evaluations => CharaInfo.evaluation_info_array.ToDictionary(x => x.target_id, x => x).ToFrozenDictionary();
    public int Month => ((Turn - 1) % 24) / 2 + 1;
    public string HalfMonth => Turn % 2 == 0 ? "后半" : "前半";
    public SingleModeTurnData GetCommonResponse() => resp;

    static int ReviseOver1200(int value) => value > 1200 ? value * 2 - 1200 : value;
}

internal sealed class TurnInfoPioneer(SingleModeTurnData resp) : TurnInfo(resp)
{
    public static readonly int[] TrainIds = [101, 105, 102, 103, 106, 3601, 3602, 3603, 3604, 3605];
    public static readonly FrozenDictionary<int, int> ToTrainId = new Dictionary<int, int>
    {
        [101] = 101, [105] = 105, [102] = 102, [103] = 103, [106] = 106,
        [3601] = 101, [3602] = 105, [3603] = 102, [3604] = 103, [3605] = 106
    }.ToFrozenDictionary();
    public static readonly FrozenDictionary<int, int> ToTrainIndex = new Dictionary<int, int>
    {
        [101] = 0, [105] = 1, [102] = 2, [103] = 3, [106] = 4,
        [3601] = 0, [3602] = 1, [3603] = 2, [3604] = 3, [3605] = 4
    }.ToFrozenDictionary();
    public static readonly FrozenDictionary<int, int> XiahesuIds = new Dictionary<int, int>
    {
        [101] = 3601, [105] = 3602, [102] = 3603, [103] = 3604, [106] = 3605
    }.ToFrozenDictionary();

    public List<CommandInfo> CommandInfoArray { get; } =
    [
        .. resp.pioneer_data_set.command_info_array
            .Where(command => command.command_id != 3101)
            .Select(command => new CommandInfo(resp, null, command.command_id, ToTrainIndex, ToTrainId))
    ];

    public Dictionary<int, int> PointGainInfoDictionary { get; } =
        resp.pioneer_data_set.pioneer_point_gain_info_array.ToDictionary(x => x.command_id, x => x.gain_num);
}

internal sealed class CommandInfo
{
    static readonly FrozenDictionary<int, int> ToTrainIndexDefault = new Dictionary<int, int>
    {
        [101] = 0, [105] = 1, [102] = 2, [103] = 3, [106] = 4,
        [3601] = 0, [3602] = 1, [3603] = 2, [3604] = 3, [3605] = 4
    }.ToFrozenDictionary();

    public int CommandId { get; }
    public int TrainIndex { get; set; }
    public int TrainLevel { get; }
    public IEnumerable<TrainingPartner> TrainingPartners { get; }

    public CommandInfo(SingleModeTurnData resp, TurnInfo? turn, int commandId, IDictionary<int, int>? trainIndexDictionary = null, IDictionary<int, int>? toTrainIdDictionary = null)
    {
        turn ??= new TurnInfo(resp);
        CommandId = commandId;
        if ((trainIndexDictionary ?? ToTrainIndexDefault).TryGetValue(commandId, out var trainIndex))
            TrainIndex = trainIndex + 1;
        var training = resp.chara_info.training_level_info_array.FirstOrDefault(x => x.command_id == CommandId);
        TrainLevel = training != default ? training.level : 0;
        var normalCommand = resp.home_info.command_info_array.FirstOrDefault(x => x.command_id == CommandId)
            ?? resp.home_info.command_info_array.First(x => (toTrainIdDictionary ?? TurnInfoPioneer.ToTrainId).TryGetValue(CommandId, out var trainId) && x.command_id == trainId);
        TrainingPartners = normalCommand.training_partner_array
            .Select(x => new TrainingPartner(turn, x, normalCommand, toTrainIdDictionary))
            .OrderBy(x => x.Priority)
            .ToArray();
    }
}

internal enum PartnerPriority
{
    友人 = 0,
    闪 = 1,
    羁绊不足 = 2,
    其他 = 3,
    关键NPC = 5,
    默认 = 7
}

internal sealed class TrainingPartner
{
    public PartnerPriority Priority { get; private set; } = PartnerPriority.默认;
    public int Position { get; }
    public int CardId { get; }
    public string Name { get; }
    public int Friendship { get; }
    public bool IsNpc => Position is not (>= 1 and <= 6);
    public bool Shining { get; private set; }

    public TrainingPartner(TurnInfo turn, int partner, SingleModeCommandInfo command, IDictionary<int, int>? toTrainIdDictionary = null)
    {
        Position = partner;
        Friendship = turn.Evaluations.TryGetValue(Position, out var evaluation) ? evaluation.evaluation : 0;

        if (!IsNpc)
        {
            CardId = turn.SupportCards[Position];
            var supportCard = Database.Names.GetRequiredSupportCard(CardId);
            var name = Database.Names.DisplayNickname(CardId);
            if (supportCard.IsFriendCard)
            {
                Priority = PartnerPriority.友人;
            }
            else if (Friendship < 80)
            {
                Priority = PartnerPriority.羁绊不足;
            }

            var trainId = (toTrainIdDictionary ?? TurnInfoPioneer.ToTrainId)[command.command_id];
            Shining = Friendship >= 80 && supportCard.Type == trainId;

            if (Shining)
                Priority = supportCard.IsFriendCard ? PartnerPriority.友人 : PartnerPriority.闪;

            var append = Friendship < 100 ? $" {Friendship}" : string.Empty;
            Name = $"{(Shining ? "★" : string.Empty)}{name}{append}";
        }
        else
        {
            Priority = Position is >= 100 and < 1000 ? PartnerPriority.关键NPC : PartnerPriority.默认;
            Name = Database.Names.DisplayNickname(Position);
        }

        var tips = command.tips_event_partner_array.Intersect(command.training_partner_array);
        if (tips.Contains(Position))
            Name = $"!{Name}";
    }
}
