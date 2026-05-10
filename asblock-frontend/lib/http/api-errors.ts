import type { FieldValues, Path, UseFormSetError } from "react-hook-form";

/** Error entry returned by AssetBlock WebApi MapResultToActionResult for many failure statuses. */
export interface ApiErrorItem {
  identifier?: string;
  message?: string;
}

export interface ApiErrorsArrayBody {
  errors?: ApiErrorItem[];
}

const GENERIC_VALIDATION_DETAIL = "One or more validation errors occurred.";

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Maps API property path (e.g. "Description", "Request.Title") to a typical RHF field name.
 */
export function apiPropertyPathToFormField(propertyPath: string): string {
  const segment = propertyPath.includes(".")
    ? propertyPath.slice(propertyPath.lastIndexOf(".") + 1)
    : propertyPath;
  if (!segment) return propertyPath;
  return segment.charAt(0).toLowerCase() + segment.slice(1);
}

export interface ParsedApiError {
  /** Human-readable text for toasts; multiple issues joined with "; ". */
  summary: string;
  /** First message per field for react-hook-form `setError` (camelCase keys). */
  fieldErrors: Record<string, string>;
}

function collectFromValidationDictionary(
  errObj: Record<string, unknown>,
  fieldErrors: Record<string, string>,
  allMessages: string[],
): void {
  for (const [key, val] of Object.entries(errObj)) {
    const rawList = Array.isArray(val) ? val : val != null ? [val] : [];
    const strMsgs = rawList
      .map((m) => (typeof m === "string" ? m.trim() : String(m).trim()))
      .filter((m) => m.length > 0);
    if (strMsgs.length === 0) continue;
    allMessages.push(...strMsgs);
    const formKey = apiPropertyPathToFormField(key);
    if (!(formKey in fieldErrors)) {
      const first = strMsgs[0];
      if (first) fieldErrors[formKey] = first;
    }
  }
}

function collectFromErrorsArray(errors: unknown[], allMessages: string[]): void {
  for (const item of errors) {
    if (item && typeof item === "object" && "message" in item) {
      const m = (item as { message: unknown }).message;
      if (typeof m === "string" && m.trim()) {
        allMessages.push(m.trim());
        continue;
      }
    }
    if (item && typeof item === "object" && "identifier" in item) {
      const id = (item as { identifier: unknown }).identifier;
      if (typeof id === "string" && id.trim()) {
        allMessages.push(id.trim());
        continue;
      }
    }
    if (typeof item === "string" && item.trim()) {
      allMessages.push(item.trim());
    }
  }
}

/**
 * Parses AssetBlock / ASP.NET ProblemDetails + validation dictionary + legacy `{ errors: [...] }` bodies.
 */
export function parseApiErrorBody(body: unknown): ParsedApiError | undefined {
  if (!isPlainObject(body)) {
    return undefined;
  }

  const o = body;
  const fieldErrors: Record<string, string> = {};
  const allMessages: string[] = [];

  const errorsVal = o.errors;

  if (errorsVal && typeof errorsVal === "object" && !Array.isArray(errorsVal)) {
    collectFromValidationDictionary(errorsVal as Record<string, unknown>, fieldErrors, allMessages);
  }

  if (Array.isArray(errorsVal) && errorsVal.length > 0) {
    collectFromErrorsArray(errorsVal, allMessages);
  }

  if (typeof o.error === "string" && o.error.trim()) {
    allMessages.push(o.error.trim());
  }

  const unique = [...new Set(allMessages)];
  let summary = unique.join("; ");

  if (!summary && typeof o.detail === "string") {
    const d = o.detail.trim();
    if (d.length > 0 && d !== GENERIC_VALIDATION_DETAIL) {
      summary = d;
    }
  }

  if (!summary && typeof o.title === "string") {
    const t = o.title.trim();
    if (t.length > 0 && t !== "Validation failed") {
      summary = t;
    }
  }

  if (!summary) {
    return undefined;
  }

  return {
    summary,
    fieldErrors,
  };
}

export function isApiErrorsBody(value: unknown): value is ApiErrorsArrayBody {
  return (
    typeof value === "object" &&
    value !== null &&
    "errors" in value &&
    Array.isArray((value as ApiErrorsArrayBody).errors)
  );
}

/**
 * Parses JSON error body from a failed fetch; returns a short message for UI or logging.
 */
export function getMessageFromApiErrorBody(body: unknown): string | undefined {
  return parseApiErrorBody(body)?.summary;
}

/** Same as {@link getMessageFromApiErrorBody} with a guaranteed non-empty fallback. */
export function getApiErrorMessage(body: unknown, fallback: string): string {
  return getMessageFromApiErrorBody(body) ?? fallback;
}

/** Maps server validation keys (camelCase) onto react-hook-form fields. */
export function applyApiFieldErrorsToForm<T extends FieldValues>(
  setError: UseFormSetError<T>,
  fieldErrors: Record<string, string>,
): void {
  for (const [path, message] of Object.entries(fieldErrors)) {
    if (!message) continue;
    setError(path as Path<T>, { type: "server", message });
  }
}
