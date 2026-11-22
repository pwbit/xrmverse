using System.Collections.Frozen;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata;

/// <summary>
/// One Dataverse time zone (#131, ruling Z1): <c>timezonedefinition.timezonecode</c>, <c>standardname</c> (the Windows time
/// zone id), <c>bias</c> (minutes west of UTC) and <c>retiredorder</c> as observed on a licensed trial environment
/// (2026-11-15), and the IANA id xrmverse resolves it with (chosen by us from the Windows-to-IANA mapping, canonical names).
/// </summary>
public sealed record TimeZoneEntry(int Code, string StandardName, int Bias, int RetiredOrder, string IanaId);

/// <summary>
/// The time zone codes of <c>usersettings.timezonecode</c> and <c>timezonedefinition</c>, and the conversions between UTC and a
/// zone's local time (#131). A code resolves through its IANA id, else its Windows id; conversions follow the host's tz database,
/// not Dataverse's <c>timezonerule</c> rows. <c>fixtures/ui/time-zones.json</c> and the UI's <c>timeZones.ts</c> hold the same
/// code → IANA map.
/// </summary>
public static class TimeZoneCatalog
{
    /// <summary>UTC, the time zone of a new user (ruling Z2).</summary>
    public const int Utc = 92;

    public static IReadOnlyList<TimeZoneEntry> All { get; } =
    [
        new(0, "Dateline Standard Time", 720, 0, "Etc/GMT+12"),
        new(1, "Samoa Standard Time", -780, 0, "Pacific/Apia"),
        new(2, "Hawaiian Standard Time", 600, 0, "Pacific/Honolulu"),
        new(3, "Alaskan Standard Time", 540, 0, "America/Anchorage"),
        new(4, "Pacific Standard Time", 480, 0, "America/Los_Angeles"),
        new(5, "Pacific Standard Time (Mexico)", 480, 0, "America/Tijuana"),
        new(6, "UTC-11", 660, 0, "Etc/GMT+11"),
        new(7, "Aleutian Standard Time", 600, 0, "America/Adak"),
        new(8, "Marquesas Standard Time", 570, 0, "Pacific/Marquesas"),
        new(9, "UTC-09", 540, 0, "Etc/GMT+9"),
        new(10, "Mountain Standard Time", 420, 0, "America/Denver"),
        new(11, "UTC-08", 480, 0, "Etc/GMT+8"),
        new(12, "Mountain Standard Time (Mexico)", 420, 0, "America/Mazatlan"),
        new(13, "Mexico Standard Time 2", 420, 1, "America/Chihuahua"),
        new(15, "US Mountain Standard Time", 420, 0, "America/Phoenix"),
        new(20, "Central Standard Time", 360, 0, "America/Chicago"),
        new(25, "Canada Central Standard Time", 360, 0, "America/Regina"),
        new(29, "Central Standard Time (Mexico)", 360, 0, "America/Mexico_City"),
        new(30, "Mexico Standard Time", 360, 1, "America/Mexico_City"),
        new(33, "Central America Standard Time", 360, 0, "America/Guatemala"),
        new(34, "Easter Island Standard Time", 360, 0, "Pacific/Easter"),
        new(35, "Eastern Standard Time", 300, 0, "America/New_York"),
        new(40, "US Eastern Standard Time", 300, 0, "America/Indiana/Indianapolis"),
        new(43, "Haiti Standard Time", 300, 0, "America/Port-au-Prince"),
        new(44, "Cuba Standard Time", 300, 0, "America/Havana"),
        new(45, "SA Pacific Standard Time", 300, 0, "America/Bogota"),
        new(47, "Venezuela Standard Time", 240, 0, "America/Caracas"),
        new(50, "Atlantic Standard Time", 240, 0, "America/Halifax"),
        new(51, "Turks And Caicos Standard Time", 300, 0, "America/Grand_Turk"),
        new(55, "SA Western Standard Time", 240, 0, "America/La_Paz"),
        new(56, "Pacific SA Standard Time", 240, 0, "America/Santiago"),
        new(58, "Central Brazilian Standard Time", 240, 0, "America/Cuiaba"),
        new(59, "Paraguay Standard Time", 240, 0, "America/Asuncion"),
        new(60, "Newfoundland Standard Time", 210, 0, "America/St_Johns"),
        new(65, "E. South America Standard Time", 180, 0, "America/Sao_Paulo"),
        new(69, "Argentina Standard Time", 180, 0, "America/Argentina/Buenos_Aires"),
        new(70, "SA Eastern Standard Time", 180, 0, "America/Cayenne"),
        new(71, "Bahia Standard Time", 180, 0, "America/Bahia"),
        new(72, "Saint Pierre Standard Time", 180, 0, "America/Miquelon"),
        new(73, "Greenland Standard Time", 180, 0, "America/Nuuk"),
        new(74, "Montevideo Standard Time", 180, 0, "America/Montevideo"),
        new(75, "Mid-Atlantic Standard Time", 120, 0, "Etc/GMT+2"),
        new(76, "UTC-02", 120, 0, "Etc/GMT+2"),
        new(77, "Tocantins Standard Time", 180, 0, "America/Araguaina"),
        new(80, "Azores Standard Time", 60, 0, "Atlantic/Azores"),
        new(83, "Cape Verde Standard Time", 60, 0, "Atlantic/Cape_Verde"),
        new(84, "Morocco Standard Time", 0, 0, "Africa/Casablanca"),
        new(85, "GMT Standard Time", 0, 0, "Europe/London"),
        new(90, "Greenwich Standard Time", 0, 0, "Atlantic/Reykjavik"),
        new(92, "UTC", 0, 0, "Etc/UTC"),
        new(95, "Central Europe Standard Time", -60, 0, "Europe/Budapest"),
        new(100, "Central European Standard Time", -60, 0, "Europe/Warsaw"),
        new(105, "Romance Standard Time", -60, 0, "Europe/Paris"),
        new(110, "W. Europe Standard Time", -60, 0, "Europe/Berlin"),
        new(113, "W. Central Africa Standard Time", -60, 0, "Africa/Lagos"),
        new(115, "E. Europe Standard Time", -120, 0, "Europe/Chisinau"),
        new(120, "Egypt Standard Time", -120, 0, "Africa/Cairo"),
        new(125, "FLE Standard Time", -120, 0, "Europe/Kyiv"),
        new(129, "Jordan Standard Time", -120, 0, "Asia/Amman"),
        new(130, "GTB Standard Time", -120, 0, "Europe/Bucharest"),
        new(131, "Middle East Standard Time", -120, 0, "Asia/Beirut"),
        new(133, "Syria Standard Time", -120, 0, "Asia/Damascus"),
        new(134, "Turkey Standard Time", -180, 0, "Europe/Istanbul"),
        new(135, "Israel Standard Time", -120, 0, "Asia/Jerusalem"),
        new(140, "South Africa Standard Time", -120, 0, "Africa/Johannesburg"),
        new(141, "Namibia Standard Time", -120, 0, "Africa/Windhoek"),
        new(142, "West Bank Standard Time", -120, 0, "Asia/Hebron"),
        new(145, "Russian Standard Time", -180, 0, "Europe/Moscow"),
        new(150, "Arab Standard Time", -180, 0, "Asia/Riyadh"),
        new(151, "Belarus Standard Time", -180, 0, "Europe/Minsk"),
        new(155, "E. Africa Standard Time", -180, 0, "Africa/Nairobi"),
        new(158, "Arabic Standard Time", -180, 0, "Asia/Baghdad"),
        new(159, "Kaliningrad Standard Time", -120, 0, "Europe/Kaliningrad"),
        new(160, "Iran Standard Time", -210, 0, "Asia/Tehran"),
        new(165, "Arabian Standard Time", -240, 0, "Asia/Dubai"),
        new(169, "Azerbaijan Standard Time", -240, 0, "Asia/Baku"),
        new(170, "Caucasus Standard Time", -240, 0, "Asia/Yerevan"),
        new(172, "Mauritius Standard Time", -240, 0, "Indian/Mauritius"),
        new(173, "Georgian Standard Time", -240, 0, "Asia/Tbilisi"),
        new(174, "Russia Time Zone 3", -240, 0, "Europe/Samara"),
        new(175, "Afghanistan Standard Time", -270, 0, "Asia/Kabul"),
        new(176, "Astrakhan Standard Time", -240, 0, "Europe/Astrakhan"),
        new(180, "Ekaterinburg Standard Time", -300, 0, "Asia/Yekaterinburg"),
        new(184, "Pakistan Standard Time", -300, 0, "Asia/Karachi"),
        new(185, "West Asia Standard Time", -300, 0, "Asia/Tashkent"),
        new(190, "India Standard Time", -330, 0, "Asia/Kolkata"),
        new(193, "Nepal Standard Time", -345, 0, "Asia/Kathmandu"),
        new(195, "Central Asia Standard Time", -360, 0, "Asia/Almaty"),
        new(196, "Bangladesh Standard Time", -360, 0, "Asia/Dhaka"),
        new(197, "Omsk Standard Time", -360, 0, "Asia/Omsk"),
        new(200, "Sri Lanka Standard Time", -330, 0, "Asia/Colombo"),
        new(201, "N. Central Asia Standard Time", -420, 0, "Asia/Novosibirsk"),
        new(203, "Myanmar Standard Time", -390, 0, "Asia/Yangon"),
        new(205, "SE Asia Standard Time", -420, 0, "Asia/Bangkok"),
        new(207, "North Asia Standard Time", -420, 0, "Asia/Krasnoyarsk"),
        new(208, "Altai Standard Time", -420, 0, "Asia/Barnaul"),
        new(209, "W. Mongolia Standard Time", -420, 0, "Asia/Hovd"),
        new(210, "China Standard Time", -480, 0, "Asia/Shanghai"),
        new(211, "Tomsk Standard Time", -420, 0, "Asia/Tomsk"),
        new(215, "Singapore Standard Time", -480, 0, "Asia/Singapore"),
        new(220, "Taipei Standard Time", -480, 0, "Asia/Taipei"),
        new(225, "W. Australia Standard Time", -480, 0, "Australia/Perth"),
        new(227, "North Asia East Standard Time", -480, 0, "Asia/Irkutsk"),
        new(228, "Ulaanbaatar Standard Time", -480, 0, "Asia/Ulaanbaatar"),
        new(229, "North Korea Standard Time", -540, 0, "Asia/Pyongyang"),
        new(230, "Korea Standard Time", -540, 0, "Asia/Seoul"),
        new(231, "Aus Central W. Standard Time", -525, 0, "Australia/Eucla"),
        new(235, "Tokyo Standard Time", -540, 0, "Asia/Tokyo"),
        new(240, "Yakutsk Standard Time", -540, 0, "Asia/Yakutsk"),
        new(241, "Transbaikal Standard Time", -540, 0, "Asia/Chita"),
        new(245, "AUS Central Standard Time", -570, 0, "Australia/Darwin"),
        new(250, "Cen. Australia Standard Time", -570, 0, "Australia/Adelaide"),
        new(251, "Adelaide (Commonwealth Games 2006)", -570, 1, "Australia/Adelaide"),
        new(255, "AUS Eastern Standard Time", -600, 0, "Australia/Sydney"),
        new(256, "Canberra, Melbourne, Sydney (Commonwealth Games 2006)", -600, 1, "Australia/Sydney"),
        new(260, "E. Australia Standard Time", -600, 0, "Australia/Brisbane"),
        new(265, "Tasmania Standard Time", -600, 0, "Australia/Hobart"),
        new(266, "Hobart (Commonwealth Games 2006)", -600, 1, "Australia/Hobart"),
        new(270, "Vladivostok Standard Time", -600, 0, "Asia/Vladivostok"),
        new(274, "Lord Howe Standard Time", -630, 0, "Australia/Lord_Howe"),
        new(275, "West Pacific Standard Time", -600, 0, "Pacific/Port_Moresby"),
        new(276, "Bougainville Standard Time", -660, 0, "Pacific/Bougainville"),
        new(277, "Norfolk Standard Time", -660, 0, "Pacific/Norfolk"),
        new(278, "Sakhalin Standard Time", -660, 0, "Asia/Sakhalin"),
        new(279, "Russia Time Zone 10", -660, 0, "Asia/Srednekolymsk"),
        new(280, "Central Pacific Standard Time", -660, 0, "Pacific/Guadalcanal"),
        new(281, "Magadan Standard Time", -660, 0, "Asia/Magadan"),
        new(284, "UTC+12", -720, 0, "Etc/GMT-12"),
        new(285, "Fiji Standard Time", -720, 0, "Pacific/Fiji"),
        new(290, "New Zealand Standard Time", -720, 0, "Pacific/Auckland"),
        new(295, "Russia Time Zone 11", -720, 0, "Asia/Kamchatka"),
        new(299, "Chatham Islands Standard Time", -765, 0, "Pacific/Chatham"),
        new(300, "Tonga Standard Time", -780, 0, "Pacific/Tongatapu"),
        new(301, "Eastern Standard Time (Mexico)", 300, 0, "America/Cancun"),
        new(302, "Sudan Standard Time", -120, 0, "Africa/Khartoum"),
        new(303, "Magallanes Standard Time", 180, 0, "America/Punta_Arenas"),
        new(304, "Volgograd Standard Time", -240, 0, "Europe/Volgograd"),
        new(305, "Yukon Standard Time", 420, 0, "America/Whitehorse"),
        new(306, "Alberta Standard Time", 420, 0, "America/Edmonton"),
        new(307, "British Columbia Standard Time", 480, 0, "America/Vancouver"),
        new(308, "Manitoba Standard Time", 360, 0, "America/Winnipeg"),
    ];

    private static readonly FrozenDictionary<int, TimeZoneEntry> ByCode = All.ToFrozenDictionary(z => z.Code);

    /// <summary>True when <paramref name="code"/> is a Dataverse time zone code.</summary>
    public static bool Contains(int code) => ByCode.ContainsKey(code);

    /// <summary>The host's time zone of <paramref name="code"/>: by its IANA id, else by its Windows id.</summary>
    /// <exception cref="XrmdFaultException">400 for an unknown code, or one the host's tz database does not hold.</exception>
    public static TimeZoneInfo Resolve(int code)
    {
        if (!ByCode.TryGetValue(code, out var entry))
            throw XrmdFaultException.InvalidArgument($"The time zone code {code} is not a valid time zone code.");
        if (TimeZoneInfo.TryFindSystemTimeZoneById(entry.IanaId, out var zone)
            || TimeZoneInfo.TryFindSystemTimeZoneById(entry.StandardName, out zone))
            return zone;
        throw XrmdFaultException.InvalidArgument(
            $"The time zone code {code} ({entry.StandardName}, {entry.IanaId}) is not known to this server's time zone database.");
    }

    /// <summary>The local time of zone <paramref name="code"/> at the instant <paramref name="utc"/> (an unspecified kind is read as UTC).</summary>
    public static DateTime LocalFromUtc(int code, DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(instant, Resolve(code)), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The UTC instant of the local time <paramref name="local"/> of zone <paramref name="code"/> (its kind is ignored). A local
    /// time that does not exist (a spring-forward gap) moves forward by the gap: it is read with the offset before the change.
    /// An ambiguous local time (repeated when the clocks go back) is read as standard time (ruling Z5).
    /// </summary>
    public static DateTime UtcFromLocal(int code, DateTime local)
    {
        var zone = Resolve(code);
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
            return DateTime.SpecifyKind(wall - zone.GetUtcOffset(wall.AddDays(-1)), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }
}
