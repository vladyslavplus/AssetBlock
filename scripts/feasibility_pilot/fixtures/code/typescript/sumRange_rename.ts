export interface Totaller { total(values: number[]): number; }
export function accumulate<T extends number>(values: readonly T[]): number {
  return values.reduce((acc, value) => acc + value, 0);
}
export class accumulateService implements Totaller {
  total(values: number[]): number {
    return accumulate(values);
  }
}
