import 'dotenv/config'; // Absolute top of the file
import { randomUUID } from 'crypto';   // for generating unique request IDs for correlations

import express, { Request, Response, NextFunction } from 'express';
import { expressjwt } from 'express-jwt';
import jwksRsa from 'jwks-rsa';
import { createProxyMiddleware } from 'http-proxy-middleware';
import { gatewayLogger } from './logger';
export const app = express();

// -----------------------------------------------------------------------------
// TypeScript Interface Extension
// Extend the standard Express Request so TypeScript knows that expressjwt
// will inject an 'auth' object containing the 'sub' (User ID) string.
// -----------------------------------------------------------------------------
interface AuthenticatedRequest extends Request {
  auth?: {
    sub: string;
    [key: string]: any; // Allows for other JWT claims if needed
  };
}

// setup the Correlation id middleware
app.use((req: Request, res: Response, next: NextFunction) => {
  // check if there is one already from an upstream service, otherwise generate a new one
  const correlationId = req.headers['x-correlation-id'] || randomUUID();
  req.headers['x-correlation-id'] = correlationId;
  res.setHeader('X-Correlation-ID', correlationId);
  next();
});

// -----------------------------------------------------------------------------
// Global Header Sanitization
// Strip internal routing headers from incoming public requests so they cannot be spoofed
// 
app.use((req, res, next) => {
  // remove the header that this Gateway is going to inject in case some bad actor
  // is trying some hacking attempt
  delete req.headers['x-verified-user'];
  
  // could remove other internal/custom headers here as well if required
  
  next();
});

// Wire in the global gateway logger
app.use(gatewayLogger);

// -----------------------------------------------------------------------------
// Step 1. JWKS Caching config
// This grabs the JWKS signature information from Auth0 and caches it for efficient JWT token verification
// as the requests hit this gateway
// The library also handles the verification of the token's expiry,audience and issuer
// - if ok, the request proceeds to the next middleware in the pipeline
// - if not, throw error and terminate the request
// -----------------------------------------------------------------------------
const checkJwt = expressjwt({
  secret: jwksRsa.expressJwtSecret({
    jwksUri: process.env.AUTH0_JWKS_URI!,
    // caches the JWKS signature keys to improve performance and reduce network calls back to Auth0
    cache: true,      
    // enable rate limit protection to avoid exceeding the allowed number of JWKS requests to Auth0 and gateway
    // being limited. Important safeguard against DoS attacks
    // Configured to 5 JWKS requests per minute
    rateLimit: true,  
    jwksRequestsPerMinute: 5
  }),
  audience: process.env.AUTH0_AUDIENCE!,
  issuer: process.env.AUTH0_ISSUER!,
  algorithms: ['RS256']      // must set this - prevents possible tampering of the token to a weaker encryption
});

// -----------------------------------------------------------------------------
// Step 2. Enforce Policy & Inject UserId Header
// -----------------------------------------------------------------------------
const verifyAndInject = (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  // check to ensure the request has now has the decoded token claims and contains a 'sub' claim value
  if (!req.auth || !req.auth.sub) {
    // if either fails, the request is considered unauthorised so respond with a 401
    //
    // create an error object to pass to the global error handler
    // for ambiguity, create a generic error message rather than exposing the specific situation
    const error = new Error('Invalid or missing token');
    error.name = 'UnauthorizedError'; 
    return next(error);
  }
  
  // Inject the UserId/sub claim value into the request as a header to pass thru to the backend api
  req.headers['X-Verified-User'] = req.auth.sub;
  next();
};

// -----------------------------------------------------------------------------
// Step 3. The Reverse Proxy
// This middleware handles forwarding requests to the backend API, removing the Authorization header containing the original token
// -----------------------------------------------------------------------------
const apiProxy = createProxyMiddleware({
  target: process.env.BACKEND_API_URI!,
  changeOrigin: true,
  onProxyReq: (proxyReq, req, res) => {
    // Strip the original JWT so the backend never sees the token
    proxyReq.removeHeader('Authorization');
  }
});

// -----------------------------------------------------------------------------
// 4. Wire the pipeline together
// The processing flow for the route is: check the token validity -> verifyAndInject -> apiProxy
// -----------------------------------------------------------------------------
app.use('/api/users', checkJwt, verifyAndInject, apiProxy);

// -----------------------------------------------------------------------------
// Gateway Error Handler
// -----------------------------------------------------------------------------
app.use((err: Error, req: Request, res: Response, next: NextFunction) => {
  // Log the error to terminal
  //
  // gateway cannot leak any internal stack traces or sensitive information back to the client
  // just present a simple message
  //
  if (err.name === 'UnauthorizedError') {
    // this catches error where the token is missing entirely or it is invalid for whatever reason
    console.log('UnauthorisedError:', 'Invalid or missing token');
    return res.status(401).json({ error: 'Invalid or missing token' });
  }
  
  // Catch-all 500 ensures no stack traces leak to the client
  console.log('InternalServerError:', 'Internal Server Error');
  return res.status(500).json({ error: 'Internal Server Error' });
});

// a bit of conditional logic to prevent the server from starting during tests
// the tests will start the server themselves if needed
if (process.env.NODE_ENV !== 'test') {
  app.listen(process.env.GATEWAY_PORT, () => console.log(`Gateway running on port ${process.env.GATEWAY_PORT}`));
}
