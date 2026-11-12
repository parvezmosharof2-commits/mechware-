# syntax=docker/dockerfile:1.7
# Shared by the storefront and the console (build from the repo root):
#   docker build -f infra/docker/next.Dockerfile --build-arg APP=web     --build-arg PORT=3000 -t mechware-web .
#   docker build -f infra/docker/next.Dockerfile --build-arg APP=console --build-arg PORT=3001 -t mechware-console .
# NEXT_PUBLIC_* values are inlined at BUILD time, so pass them as build args (public values only, never secrets).

ARG NODE_TAG=22.23.5-alpine
FROM node:${NODE_TAG} AS base
# pnpm 10 on purpose: CI uses 10 and the committed lockfile is a pnpm 10 lockfile. pnpm 11+ ignores the security
# overrides in package.json "pnpm.overrides" (postcss, minimatch), and --frozen-lockfile would then build without them.
ARG PNPM_VERSION=10.34.9
ENV NEXT_TELEMETRY_DISABLED=1
RUN corepack enable && corepack prepare pnpm@${PNPM_VERSION} --activate
WORKDIR /repo/frontend

FROM base AS deps
# Manifests only: the install layer is reused until a dependency changes.
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
COPY frontend/apps/web/package.json            apps/web/
COPY frontend/apps/console/package.json        apps/console/
COPY frontend/packages/api-client/package.json packages/api-client/
COPY frontend/packages/ui/package.json         packages/ui/
COPY frontend/packages/config/package.json     packages/config/
RUN --mount=type=cache,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile

FROM deps AS build
ARG APP=web
ARG NEXT_PUBLIC_API_BASE_URL=http://localhost:5000
ARG NEXT_PUBLIC_SITE_URL=http://localhost:3000
ARG NEXT_PUBLIC_GOOGLE_CLIENT_ID=
ENV NEXT_PUBLIC_API_BASE_URL=$NEXT_PUBLIC_API_BASE_URL \
    NEXT_PUBLIC_SITE_URL=$NEXT_PUBLIC_SITE_URL \
    NEXT_PUBLIC_GOOGLE_CLIENT_ID=$NEXT_PUBLIC_GOOGLE_CLIENT_ID
COPY frontend/packages packages
COPY frontend/apps/${APP} apps/${APP}
# Standalone output gives a small runtime without node_modules. next.config.ts does not set it yet, so the build injects it
# when absent (monorepo root as tracing root). Remove this sed once `output: 'standalone'` is in next.config.ts.
RUN cd apps/${APP} \
 && if ! grep -q "output: 'standalone'" next.config.ts; then \
      sed -i "s|reactStrictMode: true,|reactStrictMode: true,\n  output: 'standalone',\n  outputFileTracingRoot: process.cwd() + '/../../..',|" next.config.ts \
      && grep -q "output: 'standalone'" next.config.ts; fi \
 && pnpm exec next build \
 && mkdir -p /repo/public-out && if [ -d public ]; then cp -r public/. /repo/public-out/; fi

FROM node:${NODE_TAG} AS runtime
ARG APP=web
ARG PORT=3000
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=${PORT} HOSTNAME=0.0.0.0 APP=${APP}
WORKDIR /repo
# Standalone tree keeps the monorepo layout: /repo/frontend/apps/<app>/server.js
COPY --from=build --chown=node:node /repo/frontend/apps/${APP}/.next/standalone ./
COPY --from=build --chown=node:node /repo/frontend/apps/${APP}/.next/static ./frontend/apps/${APP}/.next/static
# The console has no public/ directory, so it is copied from a folder that always exists (see mkdir in the build stage).
COPY --from=build --chown=node:node /repo/public-out/ ./frontend/apps/${APP}/public/
USER 1000
EXPOSE ${PORT}
# Next writes its image/ISR cache under .next/cache: mount a tmpfs there when the root filesystem is read-only (see compose).
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
  CMD node -e "fetch('http://127.0.0.1:'+process.env.PORT+'/').then(r=>process.exit(r.status<500?0:1)).catch(()=>process.exit(1))"
CMD ["sh", "-c", "exec node frontend/apps/${APP}/server.js"]
