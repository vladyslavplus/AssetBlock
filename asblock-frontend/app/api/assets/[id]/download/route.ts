import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

/** Headers to forward so the browser saves the file with the correct name and streams efficiently. */
const FORWARD_RESPONSE_HEADERS = ["content-type", "content-disposition", "content-length"] as const;

function buildForwardHeaders(from: Response): Headers {
  const out = new Headers();
  for (const name of FORWARD_RESPONSE_HEADERS) {
    const value = from.headers.get(name);
    if (value) {
      out.set(name, value);
    }
  }
  return out;
}

export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  const store = await cookies();
  const path = `/api/assets/${encodeURIComponent(id)}/download`;
  const res = await fetchBackendAuthorized(store, path, { method: "GET" });

  const headers = buildForwardHeaders(res);
  const body = res.body;

  if (!body) {
    const text = await res.text();
    return new NextResponse(text.length > 0 ? text : null, {
      status: res.status,
      headers,
    });
  }

  return new NextResponse(body, {
    status: res.status,
    headers,
  });
}
