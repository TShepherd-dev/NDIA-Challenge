import { Request, Response, NextFunction } from 'express';

// -----------------------------------------------------------------------------
// Gateway Logger Middleware
// Logs structured information about each request and response, including correlation ID, user ID, and performance metrics.
// -----------------------------------------------------------------------------
export const gatewayLogger = (req: Request, res: Response, next: NextFunction) => {
  // start a timer when the request is received
  const startTimer = Date.now();

  // Wait for the response to completely finish before writing the log
  res.on('finish', () => {
    const duration = Date.now() - startTimer;
    
    // Check if there is a UserId successfully attached the request
    const userId = (req as any).auth?.sub || '(no UserId)';

    const logEntry = {
      timestamp: new Date().toISOString(),
      correlationId: req.headers['x-correlation-id'],
      method: req.method,
      path: req.originalUrl,
      sourceIp: req.ip || req.socket.remoteAddress,
      userId: userId,
      statusCode: res.statusCode, // Captures 200, 401, 403, 500, etc.
      durationMs: duration
    };

    // Output structured JSON based on the response status code
    if (res.statusCode >= 500) {
      // Log server errors (5xx) as errors
      console.error(JSON.stringify(logEntry));
    } else if (res.statusCode >= 400) {
      // Log client errors (4xx) as warnings
      console.warn(JSON.stringify(logEntry));
    } else {
      // Log successful responses (2xx and 3xx) as info
      console.info(JSON.stringify(logEntry));
    }
  });

  next(); 
};