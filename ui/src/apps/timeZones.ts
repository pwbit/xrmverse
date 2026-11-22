/**
 * The Dataverse time zone codes (`usersettings.timezonecode`, #131) and the IANA ids the browser converts them with: the same
 * map as the server's `TimeZoneCatalog`, pinned by `fixtures/ui/time-zones.json` on both sides.
 */
export const IANA_BY_CODE: Readonly<Record<number, string>> = {
  0: 'Etc/GMT+12',
  1: 'Pacific/Apia',
  2: 'Pacific/Honolulu',
  3: 'America/Anchorage',
  4: 'America/Los_Angeles',
  5: 'America/Tijuana',
  6: 'Etc/GMT+11',
  7: 'America/Adak',
  8: 'Pacific/Marquesas',
  9: 'Etc/GMT+9',
  10: 'America/Denver',
  11: 'Etc/GMT+8',
  12: 'America/Mazatlan',
  13: 'America/Chihuahua',
  15: 'America/Phoenix',
  20: 'America/Chicago',
  25: 'America/Regina',
  29: 'America/Mexico_City',
  30: 'America/Mexico_City',
  33: 'America/Guatemala',
  34: 'Pacific/Easter',
  35: 'America/New_York',
  40: 'America/Indiana/Indianapolis',
  43: 'America/Port-au-Prince',
  44: 'America/Havana',
  45: 'America/Bogota',
  47: 'America/Caracas',
  50: 'America/Halifax',
  51: 'America/Grand_Turk',
  55: 'America/La_Paz',
  56: 'America/Santiago',
  58: 'America/Cuiaba',
  59: 'America/Asuncion',
  60: 'America/St_Johns',
  65: 'America/Sao_Paulo',
  69: 'America/Argentina/Buenos_Aires',
  70: 'America/Cayenne',
  71: 'America/Bahia',
  72: 'America/Miquelon',
  73: 'America/Nuuk',
  74: 'America/Montevideo',
  75: 'Etc/GMT+2',
  76: 'Etc/GMT+2',
  77: 'America/Araguaina',
  80: 'Atlantic/Azores',
  83: 'Atlantic/Cape_Verde',
  84: 'Africa/Casablanca',
  85: 'Europe/London',
  90: 'Atlantic/Reykjavik',
  92: 'Etc/UTC',
  95: 'Europe/Budapest',
  100: 'Europe/Warsaw',
  105: 'Europe/Paris',
  110: 'Europe/Berlin',
  113: 'Africa/Lagos',
  115: 'Europe/Chisinau',
  120: 'Africa/Cairo',
  125: 'Europe/Kyiv',
  129: 'Asia/Amman',
  130: 'Europe/Bucharest',
  131: 'Asia/Beirut',
  133: 'Asia/Damascus',
  134: 'Europe/Istanbul',
  135: 'Asia/Jerusalem',
  140: 'Africa/Johannesburg',
  141: 'Africa/Windhoek',
  142: 'Asia/Hebron',
  145: 'Europe/Moscow',
  150: 'Asia/Riyadh',
  151: 'Europe/Minsk',
  155: 'Africa/Nairobi',
  158: 'Asia/Baghdad',
  159: 'Europe/Kaliningrad',
  160: 'Asia/Tehran',
  165: 'Asia/Dubai',
  169: 'Asia/Baku',
  170: 'Asia/Yerevan',
  172: 'Indian/Mauritius',
  173: 'Asia/Tbilisi',
  174: 'Europe/Samara',
  175: 'Asia/Kabul',
  176: 'Europe/Astrakhan',
  180: 'Asia/Yekaterinburg',
  184: 'Asia/Karachi',
  185: 'Asia/Tashkent',
  190: 'Asia/Kolkata',
  193: 'Asia/Kathmandu',
  195: 'Asia/Almaty',
  196: 'Asia/Dhaka',
  197: 'Asia/Omsk',
  200: 'Asia/Colombo',
  201: 'Asia/Novosibirsk',
  203: 'Asia/Yangon',
  205: 'Asia/Bangkok',
  207: 'Asia/Krasnoyarsk',
  208: 'Asia/Barnaul',
  209: 'Asia/Hovd',
  210: 'Asia/Shanghai',
  211: 'Asia/Tomsk',
  215: 'Asia/Singapore',
  220: 'Asia/Taipei',
  225: 'Australia/Perth',
  227: 'Asia/Irkutsk',
  228: 'Asia/Ulaanbaatar',
  229: 'Asia/Pyongyang',
  230: 'Asia/Seoul',
  231: 'Australia/Eucla',
  235: 'Asia/Tokyo',
  240: 'Asia/Yakutsk',
  241: 'Asia/Chita',
  245: 'Australia/Darwin',
  250: 'Australia/Adelaide',
  251: 'Australia/Adelaide',
  255: 'Australia/Sydney',
  256: 'Australia/Sydney',
  260: 'Australia/Brisbane',
  265: 'Australia/Hobart',
  266: 'Australia/Hobart',
  270: 'Asia/Vladivostok',
  274: 'Australia/Lord_Howe',
  275: 'Pacific/Port_Moresby',
  276: 'Pacific/Bougainville',
  277: 'Pacific/Norfolk',
  278: 'Asia/Sakhalin',
  279: 'Asia/Srednekolymsk',
  280: 'Pacific/Guadalcanal',
  281: 'Asia/Magadan',
  284: 'Etc/GMT-12',
  285: 'Pacific/Fiji',
  290: 'Pacific/Auckland',
  295: 'Asia/Kamchatka',
  299: 'Pacific/Chatham',
  300: 'Pacific/Tongatapu',
  301: 'America/Cancun',
  302: 'Africa/Khartoum',
  303: 'America/Punta_Arenas',
  304: 'Europe/Volgograd',
  305: 'America/Whitehorse',
  306: 'America/Edmonton',
  307: 'America/Vancouver',
  308: 'America/Winnipeg',};

/** The IANA id of a time zone code; throws for a code that is not one. */
export function ianaOf(code: number): string {
  const iana = IANA_BY_CODE[code];
  if (!iana) throw new Error(`The time zone code ${code} is not a time zone code.`);
  return iana;
}

/** A date and time as a clock in some time zone shows it (`month` 1 to 12). */
export interface WallClock {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
}

const formatters = new Map<string, Intl.DateTimeFormat>();
function formatter(zone: string): Intl.DateTimeFormat {
  let f = formatters.get(zone);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', {
      timeZone: zone,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
    });
    formatters.set(zone, f);
  }
  return f;
}

/** The wall clock of `zone` at the instant `instant`. */
export function wallClockOf(instant: Date, zone: string): WallClock {
  const parts = Object.fromEntries(formatter(zone).formatToParts(instant).map((p) => [p.type, p.value]));
  return { year: Number(parts.year), month: Number(parts.month), day: Number(parts.day), hour: Number(parts.hour), minute: Number(parts.minute) };
}

const MINUTE = 60_000;
const DAY = 24 * 60 * MINUTE;
/** The wall clock read as if it were UTC, in milliseconds. */
const asUtc = (w: WallClock): number => {
  const d = new Date(Date.UTC(2000, w.month - 1, w.day, w.hour, w.minute));
  d.setUTCFullYear(w.year); // Date.UTC maps years 0-99 to 1900-1999
  return d.getTime();
};

/** The offset of `zone` east of UTC at the instant `at`, in minutes (Rome in summer: 120). */
export function offsetMinutes(zone: string, at: Date): number {
  const floored = Math.floor(at.getTime() / MINUTE) * MINUTE;
  return Math.round((asUtc(wallClockOf(new Date(floored), zone)) - floored) / MINUTE);
}

const same = (a: WallClock, b: WallClock) =>
  a.year === b.year && a.month === b.month && a.day === b.day && a.hour === b.hour && a.minute === b.minute;

/**
 * The instant at which `zone`'s clock shows `w` (ruling Z5, as the server's `UtcFromLocal`): a time in a spring-forward gap is
 * read with the offset before the change (it moves forward by the gap); a time shown twice is read as standard time (the
 * smaller offset).
 */
export function wallClockToUtc(w: WallClock, zone: string): Date {
  const local = asUtc(w);
  const before = offsetMinutes(zone, new Date(local - DAY));
  const after = offsetMinutes(zone, new Date(local + DAY));
  const valid = [...new Set([before, after])]
    .map((offset) => ({ offset, instant: local - offset * MINUTE }))
    .filter((c) => same(wallClockOf(new Date(c.instant), zone), w))
    .sort((a, b) => a.offset - b.offset);
  return new Date(valid.length > 0 ? valid[0]!.instant : local - before * MINUTE);
}

/** The zone's generic name in `locale` (`Intl`, e.g. "Central European Time"); `UTC` for UTC. */
export function zoneLabel(zone: string, locale: string): string {
  if (zone === 'Etc/UTC' || zone === 'UTC') return 'UTC';
  const part = new Intl.DateTimeFormat(locale, { timeZone: zone, timeZoneName: 'longGeneric' })
    .formatToParts(new Date())
    .find((p) => p.type === 'timeZoneName');
  return part?.value ?? zone;
}

const pad = (n: number, width = 2) => String(n).padStart(width, '0');

/** The wall clock of a form editor value (`YYYY-MM-DD` at midnight, or `YYYY-MM-DDTHH:mm`); null for any other text. */
export function editorWallClock(text: string): WallClock | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2}))?$/.exec(text.trim());
  if (!m) return null;
  return { year: Number(m[1]), month: Number(m[2]), day: Number(m[3]), hour: Number(m[4] ?? 0), minute: Number(m[5] ?? 0) };
}

/** A wall clock as a form editor value: `YYYY-MM-DD`, or `YYYY-MM-DDTHH:mm`. */
export function editorText(w: WallClock, dateOnly: boolean): string {
  const date = `${pad(w.year, 4)}-${pad(w.month)}-${pad(w.day)}`;
  return dateOnly ? date : `${date}T${pad(w.hour)}:${pad(w.minute)}`;
}

/** An instant as the Web API takes a date and time: `YYYY-MM-DDTHH:mm:ssZ`. */
export const utcText = (instant: Date): string => `${instant.toISOString().slice(0, 19)}Z`;
