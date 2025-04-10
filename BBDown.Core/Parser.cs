using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using static BBDown.Core.Logger;
using static BBDown.Core.Util.HTTPUtil;
using static BBDown.Core.Entity.Entity;
using System.Security.Cryptography;
using BBDown.Core.Entity;

namespace BBDown.Core;

public static partial class Parser
{
    public static string WbiSign(string api)
    {
        return $"{api}&w_rid=" + string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(api + Config.WBI)).Select(i => i.ToString("x2")).ToArray());
    }

    private static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, string qn = "0")
    {
        LogDebug("aid={0},cid={1},epId={2},tvApi={3},IntlApi={4},appApi={5},qn={6}", aid, cid, epId, tvApi, intl, appApi, qn);

        if (intl) return await GetPlayJsonAsync(aid, cid, epId, qn);

        bool cheese = aidOri.StartsWith("cheese:");
        bool bangumi = cheese || aidOri.StartsWith("ep:");
        LogDebug("bangumi={0},cheese={1}", bangumi, cheese);

        if (appApi) return await AppHelper.DoReqAsync(aid, cid, epId, qn, bangumi, encoding, Config.TOKEN);

        string prefix = tvApi ? bangumi ? "api.snm0516.aisee.tv/pgc/player/api/playurltv" : "api.snm0516.aisee.tv/x/tv/playurl"
            : bangumi ? $"{Config.HOST}/pgc/player/web/v2/playurl" : "api.bilibili.com/x/player/wbi/playurl";
        prefix = $"https://{prefix}?";

        string api;
        if (tvApi)
        {
            StringBuilder apiBuilder = new();
            if (Config.TOKEN != "") apiBuilder.Append($"access_key={Config.TOKEN}&");
            apiBuilder.Append($"appkey=4409e2ce8ffd12b8&build=106500&cid={cid}&device=android");
            if (bangumi) apiBuilder.Append($"&ep_id={epId}&expire=0");
            apiBuilder.Append($"&fnval=4048&fnver=0&fourk=1&mid=0&mobi_app=android_tv_yst");
            apiBuilder.Append($"&object_id={aid}&platform=android&playurl_type=1&qn={qn}&ts={GetTimeStamp(true)}");
            
            // 添加限免相关参数
            if (Config.TOKEN == "" && bangumi) 
            {
                apiBuilder.Append("&module=bangumi&session=&try_look=1");
            }
            
            api = $"{prefix}{apiBuilder}&sign={GetSign(apiBuilder.ToString(), false)}";
        }
        else
        {
            StringBuilder apiBuilder = new();
            apiBuilder.Append($"support_multi_audio=true&from_client=BROWSER&avid={aid}&cid={cid}&fnval=4048&fnver=0&fourk=1");
            if (Config.AREA != "") apiBuilder.Append($"&access_key={Config.TOKEN}&area={Config.AREA}");
            apiBuilder.Append($"&otype=json&qn={qn}");
            if (bangumi) apiBuilder.Append($"&module=bangumi&ep_id={epId}&session=");
            if (Config.COOKIE == "") apiBuilder.Append("&try_look=1");
            apiBuilder.Append($"&wts={GetTimeStamp(true)}");
            api = prefix + (bangumi ? apiBuilder.ToString() : WbiSign(apiBuilder.ToString()));
        }

        //课程接口
        if (cheese) api = api.Replace("/pgc/", "/pugv/");

        string webJson = await GetWebSourceAsync(api);
        
        // 限免内容特殊处理
        if (webJson.Contains("\"大会员专享限制\"") || webJson.Contains("\"该视频需要大会员\""))
        {
            Log("检测到大会员限制，尝试使用限免参数获取...");
            // 尝试使用限免参数重新获取
            string newQn = GetTrialQn(qn);
            if (newQn != qn)
            {
                Log($"尝试降低清晰度到{Config.qualitys[newQn]}获取限免内容...");
                webJson = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intl, appApi, newQn);
            }
        }
        
        //以下情况从网页源代码尝试解析
        if (webJson.Contains("\"大会员专享限制\""))
        {
            Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            string webUrl = "https://www.bilibili.com/bangumi/play/ep" + epId;
            string webSource = await GetWebSourceAsync(webUrl);
            webJson = PlayerJsonRegex().Match(webSource).Groups[1].Value;
        }
        return webJson;
    }

    private static string GetTrialQn(string currentQn)
    {
        // 限免内容可能支持的清晰度列表，按优先级排序
        string[] trialQualities = new[] {"120", "116", "112", "80", "64", "32", "16"};
        
        // 如果当前清晰度已经是限免支持的，则保持不变
        if (trialQualities.Contains(currentQn))
            return currentQn;
            
        // 否则返回第一个支持的清晰度
        foreach (var qn in trialQualities)
        {
            if (Config.qualitys.ContainsKey(qn))
                return qn;
        }
        
        return currentQn;
    }

    private static async Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn, string code = "0")
    {
        bool isBiliPlus = Config.HOST != "api.bilibili.com";
        string api = $"https://{(isBiliPlus ? Config.HOST : "api.biliintl.com")}/intl/gateway/v2/ogv/playurl?";

        StringBuilder paramBuilder = new();
        if (Config.TOKEN != "") paramBuilder.Append($"access_key={Config.TOKEN}&");
        paramBuilder.Append($"aid={aid}");
        if (isBiliPlus) paramBuilder.Append($"&appkey=7d089525d3611b1c&area={(Config.AREA == "" ? "th" : Config.AREA)}");
        paramBuilder.Append($"&cid={cid}&ep_id={epId}&platform=android&prefer_code_type={code}&qn={qn}");
        if (isBiliPlus) paramBuilder.Append($"&ts={GetTimeStamp(true)}");

        paramBuilder.Append("&s_locale=zh_SG");
        string param = paramBuilder.ToString();
        api += (isBiliPlus ? $"{param}&sign={GetSign(param, true)}" : param);

        string webJson = await GetWebSourceAsync(api);
        return webJson;
    }

    public static async Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, string qn = "0")
    {
        var intlCode = "0";
        ParsedResult parsedResult = new();

        //调用解析
        parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, qn);

        LogDebug(parsedResult.WebJsonString);

        // 限免内容重试逻辑
        if (parsedResult.WebJsonString.Contains("\"大会员专享限制\"") || parsedResult.WebJsonString.Contains("\"该视频需要大会员\""))
        {
            Log("检测到大会员限制，尝试使用限免参数获取...");
            string newQn = GetTrialQn(qn);
            if (newQn != qn)
            {
                Log($"尝试降低清晰度到{Config.qualitys[newQn]}获取限免内容...");
                parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, newQn);
            }
        }

        startParsing:
        var respJson = JsonDocument.Parse(parsedResult.WebJsonString);
        var data = respJson.RootElement;

        //intl接口
        if (parsedResult.WebJsonString.Contains("\"stream_list\""))
        {
            // ... 保持原有国际版接口解析逻辑不变 ...
        }

        // ... 保持其余解析逻辑不变 ...

        return parsedResult;
    }

    /// <summary>
    /// 编码转换
    /// </summary>
    private static string GetVideoCodec(string code)
    {
        return code switch
        {
            "13" => "AV1",
            "12" => "HEVC",
            "7" => "AVC",
            _ => "UNKNOWN"
        };
    }

    private static string GetMaxQn()
    {
        // 对于限免内容，优先尝试这些清晰度
        string[] trialQualities = new[] {"120", "116", "112", "80", "64"};
        foreach (var qn in trialQualities)
        {
            if (Config.qualitys.ContainsKey(qn))
                return qn;
        }
        return Config.qualitys.Keys.First();
    }

    private static string GetTimeStamp(bool bflag)
    {
        DateTimeOffset ts = DateTimeOffset.Now;
        return bflag ? ts.ToUnixTimeSeconds().ToString() : ts.ToUnixTimeMilliseconds().ToString();
    }

    private static string GetSign(string parms, bool isBiliPlus)
    {
        string toEncode = parms + (isBiliPlus ? "acd495b248ec528c2eed1e862d393126" : "59b43e04ad6965f34319062b478f83dd");
        return string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(toEncode)).Select(i => i.ToString("x2")).ToArray());
    }

    [GeneratedRegex("window.__playinfo__=([\\s\\S]*?)<\\/script>")]
    private static partial Regex PlayerJsonRegex();
    [GeneratedRegex("http.*:\\d+")]
    private static partial Regex BaseUrlRegex();
}
