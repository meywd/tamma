import app from './index'
import { describe, it, expect } from 'vitest'
import { getTestDatabase } from './test-utils/db'

describe('Auth Endpoints', () => {
  it('should register a user successfully without an organization', async () => {
    const res = await app.request('/auth/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'test@example.com',
        password: 'password123',
        firstName: 'Test',
        lastName: 'User',
      }),
    })
    expect(res.status).toBe(200)
    const body = await res.json()
    expect(body.message).toBe('User registered successfully')
    expect(body.user.email).toBe('test@example.com')

    const db = await getTestDatabase()
    const user = await db('users').where('email', 'test@example.com').first()
    expect(user).toBeDefined()
    expect(user.email).toBe('test@example.com')
  })

  it('should register a user successfully with an organization', async () => {
    const res = await app.request('/auth/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'test2@example.com',
        password: 'password123',
        firstName: 'Test',
        lastName: 'User 2',
        organizationName: 'My Test Org',
      }),
    })
    expect(res.status).toBe(200)
    const body = await res.json()
    expect(body.message).toBe('User registered successfully')
    expect(body.user.email).toBe('test2@example.com')
    expect(body.organization.name).toBe('My Test Org')

    const db = await getTestDatabase()
    const user = await db('users').where('email', 'test2@example.com').first()
    expect(user).toBeDefined()

    const org = await db('organizations').where('name', 'My Test Org').first()
    expect(org).toBeDefined()

    const userOrg = await db('user_organizations')
      .where({ user_id: user.id, organization_id: org.id })
      .first()
    expect(userOrg).toBeDefined()
    expect(userOrg.role).toBe('owner')
  })

  it('should return validation error for invalid email', async () => {
    const res = await app.request('/auth/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'invalid-email',
        password: 'password123',
        firstName: 'Test',
        lastName: 'User',
      }),
    })
    expect(res.status).toBe(400)
    const body = await res.json()
    expect(body.error).toBe('Validation failed')
  })

  it('should return validation error for short password', async () => {
    const res = await app.request('/auth/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'test@example.com',
        password: '123',
        firstName: 'Test',
        lastName: 'User',
      }),
    })
    expect(res.status).toBe(400)
    const body = await res.json()
    expect(body.error).toBe('Validation failed')
  })
})