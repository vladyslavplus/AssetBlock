import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

export const ROOT = path.resolve(__dirname, "../../..");
export const BACKEND_DIR = path.join(ROOT, "asblock-backend");
export const FRONTEND_DIR = path.join(ROOT, "asblock-frontend");
export const BACKEND_SLN = path.join(BACKEND_DIR, "asblock-backend.slnx");
export const POLICY_JSON = path.join(ROOT, "dependency-policy.json");
export const EXCEPTIONS_JSON = path.join(ROOT, "dependency-exceptions.json");
export const NOTICES_PATH = path.join(ROOT, "THIRD-PARTY-NOTICES.md");
export const SBOM_DIR = path.join(ROOT, "artifacts", "sbom");
export const BACKEND_SBOM = path.join(SBOM_DIR, "backend.cdx.json");
export const FRONTEND_SBOM = path.join(SBOM_DIR, "frontend.cdx.json");
export const GOVERNANCE_DIAG_DIR = path.join(ROOT, "artifacts", "dependency-governance");
export const GENERATED_NOTICES_DIAG = path.join(
  GOVERNANCE_DIAG_DIR,
  "THIRD-PARTY-NOTICES.generated.md",
);
export const NOTICES_DIFF_DIAG = path.join(GOVERNANCE_DIAG_DIR, "THIRD-PARTY-NOTICES.diff");
