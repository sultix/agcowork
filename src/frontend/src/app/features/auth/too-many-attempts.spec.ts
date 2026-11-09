import { tooManyAttemptsMessage } from './too-many-attempts';

describe('tooManyAttemptsMessage', () => {
  it.each([
    [60, 'Zu viele Versuche. Bitte in 1 Minute erneut versuchen.'],
    [61, 'Zu viele Versuche. Bitte in 2 Minuten erneut versuchen.'],
    [300, 'Zu viele Versuche. Bitte in 5 Minuten erneut versuchen.'],
    [900, 'Zu viele Versuche. Bitte in 15 Minuten erneut versuchen.'],
  ])('retryAfterSeconds_%s_RoundsUpToWholeMinutes', (seconds, expected) => {
    expect(tooManyAttemptsMessage(seconds)).toBe(expected);
  });

  it.each([[1], [45], [59]])(
    'retryAfterSeconds_%s_UnderAMinute_AsksToTryAgainShortly',
    (seconds) => {
      expect(tooManyAttemptsMessage(seconds)).toBe(
        'Zu viele Versuche. Bitte gleich erneut versuchen.',
      );
    },
  );

  it.each([[undefined], [0], [-5], [Number.NaN]])(
    'retryAfterSeconds_%s_AsksToTryLater',
    (seconds) => {
      expect(tooManyAttemptsMessage(seconds)).toBe(
        'Zu viele Versuche. Bitte später erneut versuchen.',
      );
    },
  );
});
