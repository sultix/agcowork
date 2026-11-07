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
};
