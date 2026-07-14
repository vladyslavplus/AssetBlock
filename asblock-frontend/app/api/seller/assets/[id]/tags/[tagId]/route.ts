import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function DELETE(
  _request: Request,
  context: { params: Promise<{ id: string; tagId: string }> },
) {
  const { id, tagId } = await context.params;
  const store = await cookies();
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(id)}/tags/${encodeURIComponent(tagId)}`,
    { method: "DELETE" },
  );
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
