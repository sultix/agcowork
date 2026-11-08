const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const relative = new Intl.RelativeTimeFormat('de', { numeric: 'auto' });
const absolute = new Intl.DateTimeFormat('de-DE', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});
const dateOnly = new Intl.DateTimeFormat('de-DE', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});

/**
 * Short German age of a timestamp: "gerade eben", "vor 5 Minuten", "vor 3 Stunden",
 * "gestern", "vor 4 Tagen"; from a week on the date (`08.11.2026`).
 */
export function relativeTime(iso: string, now: number): string {
  const at = Date.parse(iso);
  if (Number.isNaN(at)) {
    return '';
  }
  const age = now - at;
  if (age < MINUTE) {
    return 'gerade eben';
  }
  if (age < HOUR) {
    return relative.format(-Math.floor(age / MINUTE), 'minute');
  }
  if (age < DAY) {
    return relative.format(-Math.floor(age / HOUR), 'hour');
  }
  if (age < 7 * DAY) {
    return relative.format(-Math.floor(age / DAY), 'day');
  }
  return dateOnly.format(at);
}

/** Date and time for tooltips, e.g. `08.11.2026, 09:30`. */
export function absoluteTime(iso: string): string {
  const at = Date.parse(iso);
  return Number.isNaN(at) ? '' : absolute.format(at);
}
