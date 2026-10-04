export function formatIsoDate(epochMs) {
  const date = new Date(epochMs);
  return date.toISOString().slice(0, 10);
}
