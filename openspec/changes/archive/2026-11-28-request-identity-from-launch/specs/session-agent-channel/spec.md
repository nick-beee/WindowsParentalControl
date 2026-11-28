# Spec Delta

## ADDED Requirements

### Requirement: The Agent Channel gives a Request Token
The service SHALL answer a Request Token request on an Agent Channel with a new Request Token for the launch record of that channel, only if the user of the launch record is a Restricted User and the agent of the record runs. The token SHALL be at least 32 random bytes. A new token SHALL end the earlier token of the same launch record. The service SHALL keep only a hash of each token. It SHALL ignore a user name or a session ID in the message.

#### Scenario: Token for the child
- **WHEN** the Session Agent of the child asks for a Request Token
- **THEN** the service answers with a token that names no user
- **AND** a request with that token to `/api/request` counts as a request of the child

#### Scenario: Second token
- **WHEN** the Session Agent of the child asks for a Request Token two times
- **THEN** only the second token is valid

#### Scenario: Token request that names another user
- **WHEN** the agent of the child sends a Request Token request that names the user `Mama`
- **THEN** the token of the answer is valid only for the child

## MODIFIED Requirements

### Requirement: No limit depends on data of the Session Agent
The child controls its Session Agent, so the service SHALL NOT use data from the agent for Category Usage, the set of Foreground Window exes, App Usage, the Allowance Time, a Category Limit, a suspension, or a resume because time is available. Agent data SHALL only show texts, mute and minimize windows, answer prompts, add screen captures, ask for a Session End Hold under its rate limit, and get a Request Token that proves only the user of a Grace Request.

#### Scenario: Agent with injected code
- **WHEN** the child injects code into its Session Agent that reports a Study exe while a game is in front
- **THEN** the Games usage increases as without the injected code
