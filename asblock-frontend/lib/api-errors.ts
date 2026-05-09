/** Error entry returned by AssetBlock WebApi MapResultToActionResult for many failure statuses. */
export interface ApiErrorItem {
  identifier?: string;
  message?: string;
}

export interface ApiErrorsBody {
  errors?: ApiErrorItem[];
}

export function isApiErrorsBody(value: unknown): value is ApiErrorsBody {
  return (
    typeof value === "object" &&
    value !== null &&
    "errors" in value &&
    Array.isArray((value as ApiErrorsBody).errors)
  );
}

/**
 * Parses JSON error body from a failed fetch; returns a short message for UI or logging.
 */
export function getMessageFromApiErrorBody(body: unknown): string | undefined {
  if (!isApiErrorsBody(body)) {
    return undefined;
  }
  const first = body.errors?.[0];
  if (!first) {
    return undefined;
  }
  return first.message ?? first.identifier;
}
