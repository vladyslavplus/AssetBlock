import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import type { ZodError } from "zod";
import { loginFormSchema } from "@/lib/auth/schemas";
import { tokensResponseSchema } from "@/lib/auth/tokens-schema";
import { postAuthJson } from "@/lib/server/auth-backend";
import { setAuthCookies } from "@/lib/server/auth-cookies";

function zodToErrorBody(error: ZodError) {
  return {
    errors: error.issues.map((i) => ({
      identifier: i.path.length ? i.path.join(".") : "request",
      message: i.message,
    })),
  };
}

export async function POST(request: Request) {
  let json: unknown;
  try {
    json = await request.json();
  } catch {
    return NextResponse.json(
      { errors: [{ identifier: "body", message: "Invalid JSON body" }] },
      { status: 400 },
    );
  }

  const parsed = loginFormSchema.safeParse(json);
  if (!parsed.success) {
    return NextResponse.json(zodToErrorBody(parsed.error), { status: 400 });
  }

  const { ok, status, data } = await postAuthJson("login", {
    email: parsed.data.email,
    password: parsed.data.password,
  });

  if (!ok) {
    return NextResponse.json(data, { status });
  }

  const tokens = tokensResponseSchema.safeParse(data);
  if (!tokens.success) {
    return NextResponse.json(
      { errors: [{ identifier: "server", message: "Unexpected login response shape" }] },
      { status: 502 },
    );
  }

  const store = await cookies();
  setAuthCookies(store, tokens.data);
  return NextResponse.json({ ok: true });
}
