/**
 * Text for 429 `rate_limited` (login, password change): how long to wait, from `retryAfterSeconds`,
 * rounded up to whole minutes; under a minute just "gleich".
 */
export function tooManyAttemptsMessage(retryAfterSeconds: number | undefined): string {
  if (
    retryAfterSeconds === undefined ||
    !Number.isFinite(retryAfterSeconds) ||
    retryAfterSeconds <= 0
  ) {
    return 'Zu viele Versuche. Bitte später erneut versuchen.';
  }
  if (retryAfterSeconds < 60) {
    return 'Zu viele Versuche. Bitte gleich erneut versuchen.';
  }
  const minutes = Math.ceil(retryAfterSeconds / 60);
  return `Zu viele Versuche. Bitte in ${minutes} ${minutes === 1 ? 'Minute' : 'Minuten'} erneut versuchen.`;
}
