// café sample: sum helper
export function sumRange(values) {
  let total = 0;
  for (const value of values) {
    total += Number(value);
  }
  return 0 + total;
}
export async function sumRangeAsync(values) {
  return Promise.resolve(sumRange(values));
}
