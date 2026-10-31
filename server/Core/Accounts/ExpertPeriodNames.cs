namespace Brs.Local.Accounts;
public sealed record ExpertPeriodOption(int QuestTypeId,string Name);
public sealed partial class AccountStore
{
    // Exact ChapterName values from original QuestTypeMaster.
    private static readonly ExpertPeriodOption[] ExpertPeriodOptions=[
        new(300101,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 \u300eORDER -\u96e3\u6575\u653b\u7565 No.1-\u300f"),
        new(300201,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 \u300eORDER -\u96e3\u6575\u653b\u7565 No.2-\u300f"),
        new(300202,"\u9ad8\u5ea6\u7cbe\u92ed\u6226  feat.\u6551\u4e16\u306e\u65cb\u5f8b"),
        new(300203,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 \u300eORDER -\u96e3\u6575\u653b\u7565 No.3-\u300f"),
        new(300204,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 \u300eORDER -\u96e3\u6575\u653b\u7565 No.4-\u300f"),
        new(300205,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 \u300eORDER -\u96e3\u6575\u653b\u7565 No.5-\u300f"),
        new(300206,"\u9ad8\u5ea6\u7cbe\u92ed\u6226 feat.\uff18\uff16\u2015\u30a8\u30a4\u30c6\u30a3\u30b7\u30c3\u30af\u30b9\u2015")
    ];
}
