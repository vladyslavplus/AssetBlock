/** @type {import('lint-staged').Configuration} */
export default {
  "asblock-frontend/**/*.{js,jsx,mjs,cjs,ts,tsx}":
    "node scripts/git/lint-staged-runner.mjs frontend-code",
  "asblock-frontend/**/*.{json,css,md,yml,yaml}":
    "node scripts/git/lint-staged-runner.mjs frontend-data",
  "asblock-backend/**/*.cs": "node scripts/git/lint-staged-runner.mjs backend-code",
};
