import 'dotenv/config'; // Absolute top of the file
import express, { Request, Response, NextFunction } from 'express';
import { expressjwt, GetVerificationKey } from 'express-jwt';
import jwksRsa from 'jwks-rsa';
import { createProxyMiddleware } from 'http-proxy-middleware';

export const app = express();

// -----------------------------------------------------------------------------
// TypeScript Interface Extension
// We extend the standard Express Request so TypeScript knows the JWT middleware
// will inject an 'auth' object containing the 'sub' (User ID) string.
// -----------------------------------------------------------------------------
interface AuthenticatedRequest extends Request {
  auth?: {
    sub: string;
    [key: string]: any; // Allows for other JWT claims if needed
  };
}

// -----------------------------------------------------------------------------
// 1. JWKS Caching Setup
// Fetches and caches the Identity Provider's public keys.
// -----------------------------------------------------------------------------
const checkJwt = expressjwt({
  // The 'as GetVerificationKey' cast is required by modern express-jwt versions 
  // to reconcile types with the jwks-rsa library.
  secret: jwksRsa.expressJwtSecret({
    cache: true,
    rateLimit: true,
    jwksRequestsPerMinute: 5,
    jwksUri: process.env.AUTH0_JWKS_URI!
  }) as GetVerificationKey,
  audience: process.env.AUTH0_AUDIENCE!,
  issuer: process.env.AUTH0_ISSUER!,
  algorithms: ['RS256']
});

// -----------------------------------------------------------------------------
// 2. Enforce Policy & Inject Header
// -----------------------------------------------------------------------------
const verifyAndInject = (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  // TypeScript enforces safe checking of the auth object
  if (!req.auth || !req.auth.sub) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  
  // Inject the required Zero Trust header for the .NET backend
  req.headers['x-verified-user'] = req.auth.sub;
  next();
};

// -----------------------------------------------------------------------------
// 3. JSON Structured Logging
// -----------------------------------------------------------------------------
const gatewayLogger = (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  const logEntry = {
    timestamp: new Date().toISOString(),
    method: req.method,
    path: req.originalUrl,
    sourceIp: req.ip,
    userId: req.auth?.sub // Optional chaining is a clean TS/JS feature here
  };
  console.log(JSON.stringify(logEntry));
  next();
};

// -----------------------------------------------------------------------------
// 4. The Reverse Proxy
// -----------------------------------------------------------------------------
const apiProxy = createProxyMiddleware({
  target: 'http://localhost:3001', // Points to your .NET Minimal API
  changeOrigin: true,
  onProxyReq: (proxyReq, req, res) => {
    // Strip the original JWT so the backend never sees the token
    proxyReq.removeHeader('Authorization');
  }
});

// -----------------------------------------------------------------------------
// 5. Wire the pipeline together
// -----------------------------------------------------------------------------
app.use('/api/users', checkJwt, verifyAndInject, gatewayLogger, apiProxy);

// -----------------------------------------------------------------------------
// 6. Global Error Handler
// Express requires exactly 4 arguments for error handlers.
// -----------------------------------------------------------------------------
app.use((err: Error, req: Request, res: Response, next: NextFunction) => {
  // Log the error to your terminal so you can see what went wrong
  console.error('JWKS Url:', process.env.AUTH0_JWKS_URI);
  console.error('Gateway Error:', err);

  if (err.name === 'UnauthorizedError') {
    return res.status(401).json({ error: 'Invalid or missing token' });
  }
  
  // Catch-all 500 ensures no stack traces leak to the client
  return res.status(500).json({ error: 'Internal Server Error' });
});

app.listen(3000, () => console.log('Gateway running on port 3000'));
