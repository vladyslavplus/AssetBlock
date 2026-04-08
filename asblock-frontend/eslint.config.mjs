import eslintConfigPrettier from 'eslint-config-prettier'
import nextCoreWebVitals from 'eslint-config-next/core-web-vitals'
import nextTypescript from 'eslint-config-next/typescript'

const strictRules = {
  // --- Core (ESLint) ---
  eqeqeq: ['error', 'always', { null: 'ignore' }],
  'no-console': ['warn', { allow: ['warn', 'error', 'info'] }],
  'no-var': 'error',
  'prefer-const': 'error',
  'prefer-regex-literals': 'error',
  'object-shorthand': ['error', 'always'],
  'dot-notation': 'error',
  curly: ['error', 'all'],
  'no-throw-literal': 'error',
  'prefer-promise-reject-errors': 'error',
  'no-useless-rename': 'error',
  'no-duplicate-imports': 'off', // use import/no-duplicates instead

  // --- TypeScript ---
  '@typescript-eslint/no-unused-vars': [
    'error',
    {
      args: 'after-used',
      argsIgnorePattern: '^_',
      varsIgnorePattern: '^_',
      caughtErrorsIgnorePattern: '^_',
    },
  ],
  '@typescript-eslint/consistent-type-imports': [
    'error',
    { prefer: 'type-imports', fixStyle: 'separate-type-imports' },
  ],
  '@typescript-eslint/no-import-type-side-effects': 'error',
  '@typescript-eslint/array-type': ['error', { default: 'array-simple' }],
  '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],
  '@typescript-eslint/no-non-null-assertion': 'warn',
  // Type-aware rules (need parserOptions.project) omitted here — add tseslint type-checked layer later if needed.
  '@typescript-eslint/no-explicit-any': 'warn',

  // --- Imports (eslint-plugin-import from eslint-config-next) ---
  'import/first': 'error',
  'import/newline-after-import': 'error',
  'import/no-duplicates': ['error', { 'prefer-inline': false }],
  'import/no-useless-path-segments': ['error', { noUselessIndex: true }],
  'import/no-anonymous-default-export': [
    'warn',
    { allowArray: true, allowArrowFunction: false, allowAnonymousClass: false, allowAnonymousFunction: false, allowLiteral: true, allowObject: true },
  ],

  // --- React ---
  'react/jsx-no-useless-fragment': ['warn', { allowExpressions: true }],
  'react/jsx-curly-brace-presence': [
    'warn',
    { props: 'never', children: 'never' },
  ],
  'react/self-closing-comp': 'error',
}

const eslintConfig = [
  {
    ignores: ['node_modules/**', '.next/**', 'out/**', 'public/**', 'pnpm-lock.yaml'],
  },
  ...nextCoreWebVitals,
  ...nextTypescript,
  {
    files: ['**/*.{js,mjs,cjs,ts,tsx}'],
    rules: strictRules,
  },
  eslintConfigPrettier,
]

export default eslintConfig
