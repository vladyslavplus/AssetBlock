// café sample: sum helper
export function accumulate(values) {
  let total = 0;
  for (const value of values) {
    total += Number(value);
  }
  return total;
}
export async function accumulateAsync(values) {
  return Promise.resolve(accumulate(values));
}
