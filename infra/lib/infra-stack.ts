import * as cdk from 'aws-cdk-lib/core';
import * as bedrock from 'aws-cdk-lib/aws-bedrock';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as ses from 'aws-cdk-lib/aws-ses';
import { Construct } from 'constructs';

export interface PrAgentDevStackProps extends cdk.StackProps {
  /** Address the app sends email from. Omit to skip SES; the API then keeps Email:Mode = Log. */
  readonly senderEmail?: string;
  /** Where the web app is served. Cognito only redirects to this origin. */
  readonly webOrigin: string;
}

/**
 * The AWS services the first build uses while the apps themselves run on a laptop.
 *
 * Deliberately not here yet: ECS Fargate, Aurora, AgentCore Runtime and Gateway, VPN or Direct
 * Connect to SAP. They arrive when the apps move into AWS.
 */
export class PrAgentDevStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: PrAgentDevStackProps) {
    super(scope, id, props);

    // ---- Sign-in -------------------------------------------------------------------------------
    // Users are created by an administrator. To federate with the corporate IdP later, add a SAML
    // or OIDC identity provider to this pool; the apps do not change.
    const userPool = new cognito.UserPool(this, 'Users', {
      userPoolName: 'pr-agent-dev',
      selfSignUpEnabled: false,
      signInAliases: { email: true },
      signInCaseSensitive: false,
      standardAttributes: { email: { required: true, mutable: true }, fullname: { required: true, mutable: true } },
      passwordPolicy: { minLength: 12, requireLowercase: true, requireUppercase: true, requireDigits: true, requireSymbols: true },
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { otp: true, sms: false },
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // Everyone who can sign in can raise a request. These two groups add the other roles.
    // A reviewer must also be listed as reviewer of a section in the app's admin view.
    for (const [groupName, description] of [
      ['Reviewer', 'Section managers who approve or send back purchase requests'],
      ['Admin', 'PR admins who maintain rules, sections and master data mappings'],
    ]) {
      new cognito.CfnUserPoolGroup(this, `${groupName}Group`, { userPoolId: userPool.userPoolId, groupName, description });
    }

    const domain = userPool.addDomain('Domain', {
      cognitoDomain: { domainPrefix: `pr-agent-dev-${cdk.Aws.ACCOUNT_ID}` },
    });

    const webClient = userPool.addClient('WebClient', {
      userPoolClientName: 'pr-agent-web',
      generateSecret: false, // a browser app cannot keep a secret; it uses PKCE
      authFlows: { userSrp: true },
      oAuth: {
        flows: { authorizationCodeGrant: true },
        scopes: [cognito.OAuthScope.OPENID, cognito.OAuthScope.EMAIL, cognito.OAuthScope.PROFILE],
        callbackUrls: [`${props.webOrigin}/`],
        logoutUrls: [`${props.webOrigin}/`],
      },
      supportedIdentityProviders: [cognito.UserPoolClientIdentityProvider.COGNITO],
      idTokenValidity: cdk.Duration.hours(1),
      accessTokenValidity: cdk.Duration.hours(1),
      refreshTokenValidity: cdk.Duration.days(1),
      preventUserExistenceErrors: true,
    });

    // ---- Attachments and agent conversation memory ---------------------------------------------
    const bucket = new s3.Bucket(this, 'Files', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      versioned: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      lifecycleRules: [
        { id: 'expire-agent-sessions', prefix: 'agent-sessions/', expiration: cdk.Duration.days(30) },
        { id: 'expire-old-versions', noncurrentVersionExpiration: cdk.Duration.days(90) },
      ],
    });

    // ---- Email ---------------------------------------------------------------------------------
    // A new SES account is in the sandbox: it only delivers to verified addresses. Until production
    // access is granted, set Email:RedirectAllTo in the API to a verified address.
    if (props.senderEmail) {
      new ses.EmailIdentity(this, 'Sender', { identity: ses.Identity.email(props.senderEmail) });
    }

    // ---- Guardrail for the assistant -----------------------------------------------------------
    const blocked = 'I can only help with purchase requests. Please rephrase, or fill in the form by hand.';
    const guardrail = new bedrock.CfnGuardrail(this, 'AssistantGuardrail', {
      name: 'pr-agent-dev',
      description: 'Content safety and prompt-attack filter for the PR assistant',
      blockedInputMessaging: blocked,
      blockedOutputsMessaging: blocked,
      contentPolicyConfig: {
        filtersConfig: [
          ...['HATE', 'INSULTS', 'SEXUAL', 'VIOLENCE', 'MISCONDUCT'].map((type) => ({ type, inputStrength: 'HIGH', outputStrength: 'HIGH' })),
          // Prompt attacks are an input-side risk; this filter has no output setting.
          { type: 'PROMPT_ATTACK', inputStrength: 'HIGH', outputStrength: 'NONE' },
        ],
      },
    });
    const guardrailVersion = new bedrock.CfnGuardrailVersion(this, 'AssistantGuardrailVersion', {
      guardrailIdentifier: guardrail.attrGuardrailId,
      description: 'Initial version',
    });

    // ---- Outputs: copy these into the apps' local configuration (see README) --------------------
    const out = (name: string, value: string, description: string) => new cdk.CfnOutput(this, name, { value, description });
    out('Region', this.region, 'api: AWS:Region and Auth:CognitoRegion   agent: AGENT_AWS_REGION');
    out('UserPoolId', userPool.userPoolId, 'api: Auth:CognitoUserPoolId');
    out('UserPoolClientId', webClient.userPoolClientId, 'api: Auth:CognitoClientId   web: VITE_COGNITO_CLIENT_ID');
    out('CognitoAuthority', `https://cognito-idp.${this.region}.amazonaws.com/${userPool.userPoolId}`, 'web: VITE_COGNITO_AUTHORITY');
    out('UserPoolDomain', domain.baseUrl(), 'web: VITE_COGNITO_DOMAIN');
    out('BucketName', bucket.bucketName, 'api: Storage:Bucket   agent: AGENT_SESSION_BUCKET');
    out('GuardrailId', guardrail.attrGuardrailId, 'agent: AGENT_GUARDRAIL_ID');
    out('GuardrailVersion', guardrailVersion.attrVersion, 'agent: AGENT_GUARDRAIL_VERSION');
    if (props.senderEmail) out('SenderEmail', props.senderEmail, 'api: Email:From (confirm the verification email SES sends to this address)');
  }
}
