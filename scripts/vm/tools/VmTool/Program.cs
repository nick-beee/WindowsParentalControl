// Test helper for the test VM only. It reads and changes the service database directly (run it as an administrator).
using System.Security.Principal;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Domain.Categories;

var cmd = args.ElementAtOrDefault(0) ?? "";
var today = DateOnly.FromDateTime(DateTime.Now);
switch (cmd)
{
    case "restrict":
    {
        var name = args[1];
        var minutes = int.Parse(args.ElementAtOrDefault(2) ?? "300");
        var sid = new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value;
        var user = UserRepository.Upsert(sid, name, true);
        LimitRepository.Upsert(new LimitConfig { UserId = user.Id, DailyMinutes = minutes, ScheduleStart = new TimeOnly(0, 0), ScheduleEnd = new TimeOnly(23, 59) });
        Console.WriteLine($"{name} ({sid}) id={user.Id} restricted, Allowance {minutes} min, 00:00-23:59");
        break;
    }
    case "usage":
    {
        var user = UserRepository.GetByUsername(args[1])!;
        var u = UsageRepository.GetUsage(user.Id, today);
        Console.WriteLine($"session used={u?.MinutesUsed ?? 0} bonus={u?.BonusMinutes ?? 0}");
        foreach (var (c, m) in CategoryUsageRepository.GetForUserAndDate(user.Id, today)) Console.WriteLine($"{c}={m}");
        break;
    }
    case "set-usage":
    {
        var user = UserRepository.GetByUsername(args[1])!;
        var category = Enum.Parse<AppCategory>(args[2], true);
        var target = int.Parse(args[3]);
        var current = CategoryUsageRepository.GetForUserAndDate(user.Id, today).GetValueOrDefault(category);
        CategoryUsageRepository.AddMinutes(user.Id, today, category, target - current);
        Console.WriteLine($"{category} usage {current} -> {target} (only increases work)");
        break;
    }
    case "sql":
    {
        using var c = DatabaseManager.CreateConnection();
        using var q = c.CreateCommand();
        q.CommandText = args[1];
        using var r = q.ExecuteReader();
        while (r.Read())
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i).ToString())));
        break;
    }
    case "records":
        foreach (var r in SuspensionRepository.GetAll()) Console.WriteLine(r);
        break;
    case "limits":
        foreach (var (c, l) in CategoryRepository.GetLimits()) Console.WriteLine($"{c}: {l.WeekdayMinutes?.ToString() ?? "-"}/{l.WeekendMinutes?.ToString() ?? "-"}");
        foreach (var r in AppRuleRepository.GetAllRows()) Console.WriteLine($"rule {r.Rule.Id} {r.Rule.MatchType} {r.Rule.Pattern} -> {r.Rule.Category} {r.Rule.AppLimit} default={r.IsDefault}");
        break;
    case "totp":
        if (args[1] == "on" && string.IsNullOrEmpty(SettingsRepository.Get(SettingsRepository.KeyTotpSecret)))
            SettingsRepository.Set(SettingsRepository.KeyTotpSecret, ParentalControl.Core.Security.TotpService.GenerateSecret());
        SettingsRepository.Set(SettingsRepository.KeyTotpEnabled, args[1] == "on" ? "true" : "false");
        Console.WriteLine("totp_enabled=" + SettingsRepository.Get(SettingsRepository.KeyTotpEnabled));
        break;
    case "msg":
        Console.WriteLine(SettingsRepository.Get(args[1]));
        break;
    default:
        Console.WriteLine("restrict <user> [min] | usage <user> | set-usage <user> <category> <min> | records | limits | totp on|off | msg <key>");
        break;
}
