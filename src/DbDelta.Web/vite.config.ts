import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Built output lands in the API's wwwroot so one `dotnet run` serves the whole tool.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../DbDelta.Api/wwwroot', emptyOutDir: true },
  server: {
    port: 5200,
    proxy: { '/api': 'http://localhost:5199' },
  },
})
