import { defineConfig } from '@playwright/test'

// End-to-end tests drive the real stack. Start it first (see the README): Postgres, the API, the
// agent with AGENT_MODEL_PROVIDER=scripted, and `npm run dev`.
export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
})
