import { reactRouter } from "@react-router/dev/vite";
import { defineConfig } from "vite";
import path from "path";

export default defineConfig({
  plugins: [reactRouter()],
  resolve: {
    alias: {
      "~": path.resolve(__dirname, "./app"),
    },
  },
  build: {
    // Pin to vite 6's previous "modules" baseline so we don't tighten the
    // supported browser floor when bumping vite to 8 (which now defaults to
    // baseline-widely-available = Chrome 111/Safari 16.4).
    target: ["es2020", "edge88", "firefox78", "chrome87", "safari14"],
  },
  server: {
    port: parseInt(process.env.PORT || "6700"),
  },
  test: {
    globals: true,
    environment: "happy-dom",
  },
});
