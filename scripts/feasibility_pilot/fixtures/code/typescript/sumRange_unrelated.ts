export function parseQuery(text: string): Record<string, string> {
  return Object.fromEntries(new URLSearchParams(text));
}
