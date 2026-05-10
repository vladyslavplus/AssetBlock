import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { z } from "zod";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

const bodySchema = z.object({
  currentPassword: z.string().min(1, "Current password is required"),
  newPassword: z.string().min(8, "New password must be at least 8 characters"),
});

export async function POST(request: Request) {
  let json: unknown;
  try {
    json = await request.json();
  } catch {
    return NextResponse.json({ errors: [{ identifier: "body", message: "Invalid JSON" }] }, { status: 400 });
  }
  const parsed = bodySchema.safeParse(json);
  if (!parsed.success) {
    return NextResponse.json(
      {
        errors: parsed.error.issues.map((i) => ({
          identifier: i.path.join(".") || "request",
          message: i.message,
        })),
      },
      { status: 400 },
    );
  }

  const { currentPassword, newPassword } = parsed.data;
  if (currentPassword === newPassword) {
    return NextResponse.json(
      { errors: [{ identifier: "newPassword", message: "New password must differ from the current one" }] },
      { status: 400 },
    );
  }

  const store = await cookies();
  const res = await fetchBackendAuthorized(store, "/api/users/me/password", {
    method: "POST",
    body: JSON.stringify({
      currentPassword,
      newPassword,
    }),
  });

  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
