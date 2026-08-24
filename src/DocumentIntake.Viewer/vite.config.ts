import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  // Loaded without the VITE_ prefix so the function key stays on the dev server and never reaches the bundle.
  const env = loadEnv(mode, process.cwd(), '');
  const target = env.FUNCTION_APP_BASE_URL;
  const key = env.FUNCTION_APP_KEY;

  if (!target) {
    console.warn('[viewer] FUNCTION_APP_BASE_URL is not set - /api requests will fail. Copy .env.example to .env.local.');
  }

  return {
    plugins: [react()],
    server: {
      // labels.ts imports analyzers/contentunderstanding/field-map.json from the repo root.
      fs: { allow: ['..', '../..'] },
      proxy: target
        ? {
            '/api': {
              target,
              changeOrigin: true,
              secure: true,
              headers: key ? { 'x-functions-key': key } : undefined,
            },
          }
        : undefined,
    },
  };
});
