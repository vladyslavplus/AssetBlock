import { getApiErrorMessage } from "@/lib/http/api-errors";

async function throwIfNotOk(res: Response): Promise<void> {
  if (res.ok) {
    return;
  }
  const json: unknown = await res.json().catch(() => null);
  throw new Error(getApiErrorMessage(json, `Request failed (${res.status})`));
}

export async function adminPostJson(path: string, jsonBody: unknown): Promise<unknown> {
  const res = await fetch(path, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(jsonBody),
  });
  await throwIfNotOk(res);
  const text = await res.text();
  if (text.length === 0) {
    return null;
  }
  return JSON.parse(text) as unknown;
}

export async function adminPutJson(path: string, jsonBody: unknown): Promise<unknown> {
  const res = await fetch(path, {
    method: "PUT",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(jsonBody),
  });
  await throwIfNotOk(res);
  const text = await res.text();
  if (text.length === 0) {
    return null;
  }
  return JSON.parse(text) as unknown;
}

export async function adminDelete(path: string): Promise<void> {
  const res = await fetch(path, { method: "DELETE", credentials: "include" });
  await throwIfNotOk(res);
}
