import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import tseslint from 'typescript-eslint'

// Code that writes raw HTML or runs a string as code. React escapes text; these bypass it.
const rawHtml = [
  {
    selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
    message: 'Do not set raw HTML. Render the value as text.',
  },
  {
    selector: "MemberExpression[property.name=/^(innerHTML|outerHTML|insertAdjacentHTML)$/]",
    message: 'Do not write raw HTML into the DOM.',
  },
  {
    selector: "CallExpression[callee.object.name='document'][callee.property.name=/^(write|writeln)$/]",
    message: 'Do not use document.write.',
  },
  { selector: "NewExpression[callee.name='Function']", message: 'Do not run a string as code.' },
]

export default tseslint.config(
  { ignores: ['dist', 'node_modules'] },
  {
    files: ['**/*.{ts,tsx,js}'],
    extends: [js.configs.recommended, ...tseslint.configs.strict],
    languageOptions: {
      ecmaVersion: 2022,
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // The effects here start an API call and set the state after the answer. This rule cannot see the await
      // inside the called function and reports each of them.
      'react-hooks/set-state-in-effect': 'off',
      'no-eval': 'error',
      'no-implied-eval': 'error',
      'no-restricted-syntax': ['error', ...rawHtml],
    },
  },
  {
    // Tests read rows of known fixtures.
    files: ['**/*.test.{ts,tsx}'],
    rules: { '@typescript-eslint/no-non-null-assertion': 'off' },
  },
)
