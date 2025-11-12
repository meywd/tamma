import { Hono } from 'hono'
import { register } from '../controllers/auth.controller'

const authRoutes = new Hono()

authRoutes.post('/register', ...register)

export default authRoutes
