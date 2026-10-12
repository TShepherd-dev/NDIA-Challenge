import request from 'supertest';
import nock from 'nock';        // HTTP mocking library for tests - it will simulate Auth0 responses
import { afterAll, beforeAll, describe, expect, it } from '@jest/globals';
import { app } from '../src/index';

// -------------------------------------------------------------------------------------------------
// test suite for the Zero Trust Gateway Perimeter Defense
//
describe('Zero Trust Gateway Perimeter Defense', () => {
  const MOCK_AUTH0_DOMAIN = 'https://dev-tims-ndia.au.auth0.com'; // Match what is in your index.ts

  beforeAll(() => {
    // 1. Mock the OIDC Provider
    // This intercepts the outbound call to Auth0 so the tests run offline and fast.
    nock(MOCK_AUTH0_DOMAIN)
      .persist()
      .get('/.well-known/jwks.json')
      .reply(200, {
        keys: [{
          kid: 'fake-key-id',
          kty: 'RSA',
          alg: 'RS256',
          use: 'sig',
          n: 'mock-modulus',
          e: 'AQAB'
        }]
      });
  });

  afterAll(() => {
    nock.cleanAll(); // Clean up HTTP mocks after tests run
  });

  // ----------------------------------------------------------------
  // Test case: No token provided should result in 401 Unauthorized
  //
  it('Blocks traffic completely when no token is provided', async () => {
    const res = await request(app).get('/api/users');
    
    // Proves Zero Trust: No token = instant block at the perimeter
    expect(res.status).toBe(401);
    expect(res.body.error).toBe('Invalid or missing token');
  });

  // ----------------------------------------------------------------
  // Test case: Invalid JWT should result in 401 Unauthorized
  //
  it('Blocks traffic when a forged or invalid JWT is provided', async () => {
    // A structurally valid but fake token (Header.Payload.Signature)
    const fakeToken = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c';

    const res = await request(app)
      .get('/api/users')
      .set('Authorization', `Bearer ${fakeToken}`);
    
    // Proves the Gateway validates signatures against the IdP keys
    expect(res.status).toBe(401);
  });

  // ----------------------------------------------------------------
  // Test case: Distributed Tracing/Correlation ID Injection
  //
  it('Injects an X-Correlation-ID header into the response', async () => {
    const res = await request(app).get('/api/users'); // Even a 401 should get an ID
    
    // Proves the Gateway establishes a trace boundary for logs
    expect(res.headers['x-correlation-id']).toBeDefined();
    expect(typeof res.headers['x-correlation-id']).toBe('string');
  });
  
});