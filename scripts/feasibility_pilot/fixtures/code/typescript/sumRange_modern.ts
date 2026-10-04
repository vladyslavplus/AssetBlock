export type Result<T> = { ok: true; value: T } | { ok: false };
export function wrap<T>(value: T): Result<T> { return { ok: true, value }; }
