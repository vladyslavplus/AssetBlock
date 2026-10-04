export function parseQuery(text) {
  return Object.fromEntries(new URLSearchParams(text));
}
