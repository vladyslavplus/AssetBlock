export function formatIsoDate(epochMs: number): string {
  return new Date(epochMs).toISOString().slice(0, 10);
}
