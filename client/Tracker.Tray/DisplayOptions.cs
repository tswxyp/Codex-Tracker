using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexTracker.Tray;

internal readonly record struct MoneyAmount(decimal Amount,string Currency);

internal static class Money
{
    internal static readonly string[] Codes=["CNY","USD","JPY","KRW","GBP","EUR","CAD","AUD","NZD","SGD","HKD"];
    internal static int Scale(string code)=>code is "JPY" or "KRW"?1:100;
    internal static string Symbol(string code)=>code switch{
        "CNY"=>"¥","USD"=>"$","JPY"=>"JP¥","KRW"=>"₩","GBP"=>"£","EUR"=>"€",
        "CAD"=>"CA$","AUD"=>"A$","NZD"=>"NZ$","SGD"=>"S$","HKD"=>"HK$",_=>code+" "
    };
    internal static string? CurrencyFor(string? country)
    {
        if(country?.Length!=2)return null;
        try{
            var code=new RegionInfo(country.ToUpperInvariant()).ISOCurrencySymbol;
            return Codes.Contains(code)?code:null;
        }catch(ArgumentException){return null;}
    }
}

internal sealed record DisplayOptions(string Language="auto",string Currency="auto")
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    static extern int GetUserDefaultGeoName(StringBuilder region,int size);
    static string SystemRegion(){
        var country=new StringBuilder(12);
        return GetUserDefaultGeoName(country,country.Capacity)>0?country.ToString():RegionInfo.CurrentRegion.TwoLetterISORegionName;
    }
    static readonly string Region=SystemRegion();
    static string WindowsLanguage=>CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    internal string ProfitColors {get;init;}="auto";
    internal bool HasExplicitCurrency=>Money.Codes.Contains(Currency);
    internal bool RedGain=>ProfitColors switch{
        "red"=>true,"green"=>false,
        _ when HasExplicitCurrency=>Currency=="CNY",
        _=>WindowsLanguage=="zh"
    };
    internal string UiLanguage {
        get {
            var choice=Language=="auto"?WindowsLanguage:Language;
            return choice switch{"zh" or "zh-CN"=>"zh-CN","ja"=>"ja","ko"=>"ko",_=>"en"};
        }
    }
    internal string? IpCurrency {get;init;}
    internal bool UsesIp=>(Currency is "auto" or "auto-ip") && UiLanguage=="en";
    internal string DisplayCurrency=>HasExplicitCurrency?Currency:
        UsesIp && IpCurrency!=null?IpCurrency:Money.CurrencyFor(Region)??"USD";
    internal CultureInfo Culture=>CultureInfo.GetCultureInfo(UiLanguage);
    internal string T(string chinese,string english)=>UiLanguage switch{
        "zh-CN"=>chinese,"ja" or "ko"=>TrayText.Localize(english,UiLanguage),_=>english
    };
    internal string Format(string chinese,string english,params object[] args)=>string.Format(Culture,T(chinese,english),args);
    internal string SpeedEstimateNote=>T("未记录的速度按标准档估算","Standard assumed for missing speeds");
    internal string Amount(MoneyAmount money)
    {
        var (value,code)=money;
        decimal rounded=decimal.Round(value,Money.Scale(code)==1?0:2,MidpointRounding.ToEven);
        string number=Math.Abs(rounded).ToString(Money.Scale(code)==1?"N0":"N2",Culture);
        return (rounded<0?"−":"")+Money.Symbol(code)+number;
    }
    internal string Approx(MoneyAmount money)=>T("约","≈")+Amount(money);
}

// Resolve one quote table for the entire card or report, only when two different currencies occur.
internal sealed class DisplayMoney(DisplayOptions options,MarketRates? market)
{
    readonly Lazy<RateTable?> rates=new(()=>market?.ForConversion());
    internal bool AwaitingRate {get;private set;}
    internal RateTable? UsedRates=>rates.IsValueCreated?rates.Value:null;
    internal decimal? Convert(MoneyAmount amount){
        string to=options.DisplayCurrency;
        if(amount.Currency==to || amount.Amount==0)return amount.Amount;
        if(rates.Value is {} quote && Money.Codes.Contains(amount.Currency))return amount.Amount*quote.Between(amount.Currency,to);
        AwaitingRate=true;
        return null;
    }
    internal decimal? Sum(IEnumerable<MoneyAmount> amounts){
        decimal? total=0;
        foreach(var money in amounts)total+=Convert(money);
        return total;
    }
    internal string Format(decimal? amount,bool approx=false,bool signed=false){
        if(amount==null)return options.T("待计算","Pending");
        var value=new MoneyAmount(amount.Value,options.DisplayCurrency);
        string number=options.Amount(value);
        if(signed && decimal.Round(value.Amount*Money.Scale(value.Currency),0,MidpointRounding.ToEven)>0)number="+"+number;
        return approx?options.T("约","≈")+number:number;
    }
    internal string ReportAmount(decimal? amount,bool signed=false)=>amount.HasValue?Format(amount,signed:signed):"N/A";
}

internal static class IpRegion
{
    static readonly HttpClient Client=new(){Timeout=TimeSpan.FromSeconds(4)};
    internal static async Task<string?> LookupCurrency()
    {
        try{
            string country=await Client.GetStringAsync("https://ipinfo.io/country");
            return Money.CurrencyFor(country.Trim());
        }catch(Exception e) when(e is HttpRequestException or OperationCanceledException){return null;}
    }
}
