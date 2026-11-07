# Build context is the repository root: docker build -f src/frontend/Dockerfile .
# The root context is needed because `npm run build` generates the API types from
# ../backend/Agcowork.Api/openapi/v1.json (prebuild hook).
FROM node:24-alpine AS build
WORKDIR /src/frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci
COPY src/frontend/ ./
COPY src/backend/Agcowork.Api/openapi/v1.json ../backend/Agcowork.Api/openapi/v1.json
RUN npm run build

FROM nginx:1.27-alpine AS final
COPY src/frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/frontend/dist/agcowork-web/browser /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD wget -qO /dev/null http://127.0.0.1/ || exit 1
