// Dev-server proxy. Under Aspire the API endpoint arrives via the service-discovery
// variables injected by WithReference(api); standalone `npm start` falls back to the
// fixed launchSettings port of the API (5180, since 5000 is taken by AirPlay on macOS).
const target =
  process.env['services__api__http__0'] ??
  process.env['services__api__https__0'] ??
  'http://localhost:5180';

module.exports = {
  '/api': { target, secure: false, changeOrigin: true },
  '/health': { target, secure: false, changeOrigin: true },
  // Realtime hub (SignalR, AP-024): negotiate, WebSocket upgrade, long polling. A regular expression (leading ^) so the
  // SPA routes under /w/ stay with the dev server; it is matched against the URL including the query string.
  // changeOrigin false: the API compares the browser's Origin with Host for cookie connections, so Host must stay
  // the dev server's host:port.
  '^/w/[^/?]+/hub(/negotiate)?/?(\\?.*)?$': {
    target,
    secure: false,
    changeOrigin: false,
    ws: true,
  },
};
