// ADR-0014: money is integer sen. BigInt keeps every step exact; no floating point touches an amount.
export function formatSen(value: unknown): string {
  if (typeof value !== 'number' || !Number.isSafeInteger(value)) {
    throw new TypeError('Expected integer sen');
  }
  const amount = BigInt(value);
  const absolute = amount < 0n ? -amount : amount;
  const whole = (absolute / 100n).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const cents = (absolute % 100n).toString().padStart(2, '0');
  return `${amount < 0n ? '-' : ''}MYR ${whole}.${cents}`;
}
