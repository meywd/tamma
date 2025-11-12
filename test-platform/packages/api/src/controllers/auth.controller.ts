import { Context } from 'hono'
import { zValidator } from '@hono/zod-validator'
import { z } from 'zod'
import { authService } from '../services/auth.service'

const registerSchema = z.object({
  email: z.string().email(),
  password: z.string().min(8),
  firstName: z.string().min(1),
  lastName: z.string().min(1),
  organizationName: z.string().optional(),
})

export const register = [
  zValidator('json', registerSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    const userData = c.req.valid('json')
    const result = await authService.register(userData)

    return c.json({ message: 'User registered successfully', ...result })
  },
]
