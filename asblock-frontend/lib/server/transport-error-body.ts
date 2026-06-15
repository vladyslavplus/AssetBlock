import { describeServerFetchError } from "@/lib/server/fetch-errors";

export function transportErrorBody(error: unknown) {
  return {
    errors: [
      {
        identifier: "err_api_transport",
        message: describeServerFetchError(error),
      },
    ],
  };
}
