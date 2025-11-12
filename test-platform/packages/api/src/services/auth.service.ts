import { hash } from 'bcryptjs'
import { getDatabase } from '../../../../src/database/connection'
import slugify from 'slugify'

class AuthService {
  async register(userData: any) {
    const { email, password, firstName, lastName, organizationName } = userData
    const db = await getDatabase()

    // Check if user exists
    const existingUser = await db('users').where('email', email).first()
    if (existingUser) {
      throw new Error('User with this email already exists')
    }

    const hashedPassword = await hash(password, 12)

    // Create user
    const [user] = await db('users')
      .insert({
        email,
        password_hash: hashedPassword,
        password_salt: '', // Salt is included in the hash
        first_name: firstName,
        last_name: lastName,
      })
      .returning('*')

    let organization = null
    if (organizationName) {
      const slug = slugify(organizationName, { lower: true, strict: true })
      const [org] = await db('organizations')
        .insert({
          name: organizationName,
          slug,
        })
        .returning('*')
      organization = org

      await db('user_organizations').insert({
        user_id: user.id,
        organization_id: organization.id,
        role: 'owner',
        status: 'active',
      })
    }

    return {
      user,
      organization,
    }
  }
}

export const authService = new AuthService()
