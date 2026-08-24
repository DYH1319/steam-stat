import process from 'node:process'
import { createInterface } from 'node:readline'
// import { EAuthSessionGuardType, EAuthTokenPlatformType, EResult, LoginSession } from '../src'
// use the following line if you installed the module from npm
import { EAuthSessionGuardType, EAuthTokenPlatformType, EResult, LoginSession } from 'steam-session'

// Create a variable where we can store an abort function to cancel stdin input
let g_AbortPromptFunc

// We need to wrap everything in an async function since node <14.8 cannot use await in the top-level context
main()
async function main() {
  // const { EAuthSessionGuardType, EAuthTokenPlatformType, EResult, LoginSession } = await import('steam-session')

  // Prompt for credentials from the console
  const accountName = await promptAsync('Username: ')
  const password = await promptAsync('Password: ', true)

  console.warn('\nIf you\'re logging into an account using email Steam Guard and you have a machine token, enter it below. Otherwise, just hit enter.')
  const steamGuardMachineToken = await promptAsync('Machine Token: ')

  // Create our LoginSession and start a login session using our credentials. This session will be for a client login.
  const session = new LoginSession(EAuthTokenPlatformType.SteamClient)

  console.warn(`loginTimeout: ${session.loginTimeout}`)
  session.on('debug', console.error)
  session.on('debug-handler', console.error)

  const startResult = await session.startWithCredentials({
    accountName,
    password,
    steamGuardMachineToken,
  })

  // actionRequired will be true if we need to do something to finish logging in, e.g. supply a code or approve a
  // prompt on our phone.
  if (startResult.actionRequired) {
    console.warn('Action is required from you to complete this login')

    // We want to process the non-prompting guard types first, since the last thing we want to do is prompt the
    // user for input. It would be needlessly confusing to prompt for input, then print more text to the console.
    const promptingGuardTypes = [EAuthSessionGuardType.EmailCode, EAuthSessionGuardType.DeviceCode]
    const promptingGuards = startResult.validActions?.filter(action => promptingGuardTypes.includes(action.type))
    const nonPromptingGuards = startResult.validActions?.filter(action => !promptingGuardTypes.includes(action.type))

    const printGuard = async ({ type, detail }) => {
      let code

      try {
        switch (type) {
          case EAuthSessionGuardType.EmailCode:
            console.warn(`A login code has been sent to your email address at ${detail}`)
            code = await promptAsync('Code: ')
            if (code) {
              await session.submitSteamGuardCode(code)
            }
            break

          case EAuthSessionGuardType.DeviceCode:
            console.warn('You may confirm this login by providing a Steam Guard Mobile Authenticator code')
            code = await promptAsync('Code: ')
            if (code) {
              await session.submitSteamGuardCode(code)
            }
            break

          case EAuthSessionGuardType.EmailConfirmation:
            console.warn('You may confirm this login by email')
            break

          case EAuthSessionGuardType.DeviceConfirmation:
            console.warn('You may confirm this login by responding to the prompt in your Steam mobile app')
            break
        }
      }
      catch (ex) {
        if (ex.eresult === EResult.TwoFactorCodeMismatch) {
          console.warn('Incorrect Steam Guard code')
          printGuard({ type, detail })
        }
        else {
          throw ex
        }
      }
    }

    nonPromptingGuards?.forEach(guard => printGuard({ type: guard.type, detail: guard.detail }))
    promptingGuards?.forEach(guard => printGuard({ type: guard.type, detail: guard.detail }))
  }

  session.on('steamGuardMachineToken', () => {
    console.warn('\nReceived new Steam Guard machine token')
    console.warn(`Machine Token: ${session.steamGuardMachineToken}`)
  })

  session.on('authenticated', async () => {
    abortPrompt()

    console.warn('\nAuthenticated successfully! Printing your tokens now...')
    console.warn(`SteamID: ${session.steamID}`)
    console.warn(`Account name: ${session.accountName}`)
    console.warn(`Access token: ${session.accessToken}`)
    console.warn(`Refresh token: ${session.refreshToken}`)

    // We can also get web cookies now that we've negotiated a session
    const webCookies = await session.getWebCookies()
    console.warn('Web session cookies:')
    console.warn(webCookies)
  })

  session.on('timeout', () => {
    abortPrompt()

    console.warn('This login attempt has timed out.')
  })

  session.on('error', (err) => {
    abortPrompt()

    // This should ordinarily not happen. This only happens in case there's some kind of unexpected error while
    // polling, e.g. the network connection goes down or Steam chokes on something.
    console.warn(`ERROR: This login attempt has failed! ${err.message}`)
  })
}

// Nothing interesting below here, just code for prompting for input from the console.

function promptAsync(question, sensitiveInput = false): Promise<string> {
  return new Promise((resolve) => {
    const rl = createInterface({
      input: process.stdin,
      output: sensitiveInput ? undefined : process.stdout,
      completer: undefined,
      terminal: true,
    })

    g_AbortPromptFunc = () => {
      rl.close()
      resolve('')
    }

    if (sensitiveInput) {
      // We have to write the question manually if we didn't give readline an output stream
      process.stdout.write(question)
    }

    rl.question(question, (result) => {
      if (sensitiveInput) {
        // We have to manually print a newline
        process.stdout.write('\n')
      }

      g_AbortPromptFunc = null
      rl.close()
      resolve(result)
    })
  })
}

function abortPrompt() {
  if (!g_AbortPromptFunc) {
    return
  }

  g_AbortPromptFunc()
  process.stdout.write('\n')
}
