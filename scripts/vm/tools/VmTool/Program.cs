// Test helper for the test VM only. It reads and changes the service database directly (run it as an administrator).
using System.Security.Principal;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;
using ParentalControl.Domain.Categories;
using ParentalControl.Domain.AdminAccess;

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
    case "limit":
    {
        // limit <category> <minutes|none>: the same limit on weekdays and weekends.
        var category = Enum.Parse<AppCategory>(args[1], true);
        int? minutes = args[2] == "none" ? null : int.Parse(args[2]);
        CategoryRepository.SetLimit(category, new DailyLimit(minutes, minutes));
        Console.WriteLine($"{category} limit {minutes?.ToString() ?? "none"}");
        break;
    }
    case "rule":
    {
        // rule folder|file <pattern> <category>
        var match = Enum.Parse<RuleMatchType>(args[1], true);
        var id = AppRuleRepository.Insert(match, args[2], Enum.Parse<AppCategory>(args[3], true), null);
        Console.WriteLine($"rule {id}: {match} {args[2]} -> {args[3]}");
        break;
    }
    case "setting":
        // setting <key> <value>, for example: setting probe_locks_enforced 1
        SettingsRepository.Set(args[1], args[2]);
        Console.WriteLine($"{args[1]}={args[2]}");
        break;
    case "events":
        // events [type]: the events of the last 2 hours
        foreach (var e in EventRepository.GetEvents(from: DateTime.Now.AddHours(-2)))
            if (args.Length < 2 || e.EventType.ToString() == args[1])
                Console.WriteLine($"{e.Timestamp:HH:mm:ss} {e.Username} {e.EventType} {e.Details}");
        break;
    case "prompt":
    {
        // prompt <user> <text>: an Interactive Session Prompt of the parent, as the admin page sends it.
        var user = UserRepository.GetByUsername(args[1])!;
        var id = SessionPromptRepository.Create(user.Sid, user.Username, args[2]);
        Console.WriteLine($"prompt {id} for {user.Username}");
        break;
    }
    case "records":
        foreach (var r in SuspensionRepository.GetAll()) Console.WriteLine(r);
        break;
    case "limits":
        foreach (var (c, l) in CategoryRepository.GetLimits()) Console.WriteLine($"{c}: {l.WeekdayMinutes?.ToString() ?? "-"}/{l.WeekendMinutes?.ToString() ?? "-"}");
        foreach (var r in AppRuleRepository.GetAllRows()) Console.WriteLine($"rule {r.Rule.Id} {r.Rule.MatchType} {r.Rule.Pattern} -> {r.Rule.Category} {r.Rule.AppLimit} default={r.IsDefault}");
        break;
    case "totp" when args.ElementAtOrDefault(1) == "setup":
    {
        // Like a confirmed setup in the Admin app: one transaction, a new session epoch, no login limit.
        // The saved step is 2 below now, so that `totp code` gives a code that logs in at once.
        var secret = args.ElementAtOrDefault(2) ?? TestTotpSecret;
        AdminAccessRepository.CompleteSetup(secret, Totp.StepAt(DateTimeOffset.UtcNow) - 2, DateTimeOffset.UtcNow);
        Console.WriteLine($"admin sign-in set up, secret={TotpSecret.Normalize(secret)}, epoch={AdminAccessRepository.GetSessionEpoch()}");
        break;
    }
    case "totp" when args.ElementAtOrDefault(1) == "code":
    {
        // The next code that the login accepts (the replay guard refuses a used step).
        var secret = SettingsRepository.Get(SettingsRepository.KeyTotpSecret);
        if (!TotpSecret.TryDecode(secret, out var key)) { Console.Error.WriteLine("no valid secret: run `totp setup` first"); Environment.Exit(1); }
        var now = Totp.StepAt(DateTimeOffset.UtcNow);
        var step = Math.Max(now, AdminAccessRepository.GetLastUsedStep() + 1);
        if (step > now + Totp.AllowedDrift) { Console.Error.WriteLine("all current codes are used: wait 30 s"); Environment.Exit(1); }
        Console.WriteLine(Totp.ComputeCode(key, step));
        break;
    }
    case "msg":
        Console.WriteLine(SettingsRepository.Get(args[1]));
        break;
    default:
        Console.WriteLine("restrict <user> [min] | usage <user> | set-usage <user> <category> <min> | records | limits | totp setup [secret] | totp code | msg <key>");
        break;
}

partial class Program
{
    // A fixed test secret (20 bytes), so that an authenticator app on the Mac can show the same codes.
    private const string TestTotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
}
