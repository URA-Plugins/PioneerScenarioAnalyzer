using Newtonsoft.Json.Linq;
using Spectre.Console;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Plugin;

namespace PioneerScenarioAnalyzer
{
    public class PioneerScenarioAnalyzer : IPlugin
    {
        public string Name => "PioneerScenarioAnalyzer";
        public string Author => "";
        public Version Version => new(1, 0, 0);
        public Task UpdatePlugin(ProgressContext ctx)
        {
            throw new NotImplementedException();
        }

        [Analyzer(priority: 1)]
        public void Analyzer(JObject jo)
        {
            if (!jo.HasCharaInfo()) return;
            if (jo["data"] is null || jo["data"] is not JObject data) return;
            if (data["chara_info"] is null || data["chara_info"] is not JObject chara_info) return;
            if (chara_info["scenario_id"].ToInt() != 11) return;
            var state = chara_info["state"].ToInt();
            if (chara_info != null && data["home_info"]?["command_info_array"] != null && data["race_reward_info"].IsNull() && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
            {
                var @event = jo.ToObject<Gallop.SingleModeCheckEventResponse>();
                if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return;
                Handler.ParsePioneerCommandInfo(@event);
            }
        }
    }
}
