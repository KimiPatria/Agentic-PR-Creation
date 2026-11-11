import * as cdk from 'aws-cdk-lib/core';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { PrAgentDevStack } from '../lib/infra-stack';

function synth(senderEmail?: string): Template {
  const app = new cdk.App();
  const stack = new PrAgentDevStack(app, 'Test', { env: { account: '111111111111', region: 'ap-southeast-1' }, webOrigin: 'http://localhost:5173', senderEmail });
  return Template.fromStack(stack);
}

test('the user pool is closed to self sign-up and has the two role groups', () => {
  const template = synth();
  template.hasResourceProperties('AWS::Cognito::UserPool', { AdminCreateUserConfig: { AllowAdminCreateUserOnly: true } });
  template.resourceCountIs('AWS::Cognito::UserPoolGroup', 2);
  template.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Reviewer' });
  template.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Admin' });
});

test('the web client is a public PKCE client that only redirects to the web origin', () => {
  synth().hasResourceProperties('AWS::Cognito::UserPoolClient', {
    GenerateSecret: false,
    AllowedOAuthFlows: ['code'],
    CallbackURLs: ['http://localhost:5173/'],
    LogoutURLs: ['http://localhost:5173/'],
  });
});

test('the bucket is private, encrypted and TLS only', () => {
  const template = synth();
  template.hasResourceProperties('AWS::S3::Bucket', {
    PublicAccessBlockConfiguration: { BlockPublicAcls: true, BlockPublicPolicy: true, IgnorePublicAcls: true, RestrictPublicBuckets: true },
    BucketEncryption: Match.objectLike({}),
    VersioningConfiguration: { Status: 'Enabled' },
  });
  template.hasResourceProperties('AWS::S3::BucketPolicy', {
    PolicyDocument: { Statement: Match.arrayWith([Match.objectLike({ Effect: 'Deny', Condition: { Bool: { 'aws:SecureTransport': 'false' } } })]) },
  });
});

test('the guardrail filters prompt attacks on input', () => {
  synth().hasResourceProperties('AWS::Bedrock::Guardrail', {
    ContentPolicyConfig: { FiltersConfig: Match.arrayWith([{ Type: 'PROMPT_ATTACK', InputStrength: 'HIGH', OutputStrength: 'NONE' }]) },
  });
});

test('SES is only created when a sender address is given', () => {
  synth().resourceCountIs('AWS::SES::EmailIdentity', 0);
  synth('pr-agent@example.com').resourceCountIs('AWS::SES::EmailIdentity', 1);
});

test('the stack contains no compute, database or network: those arrive with the AWS deployment', () => {
  const types = Object.values(synth('a@example.com').toJSON().Resources as Record<string, { Type: string }>).map((r) => r.Type);
  for (const forbidden of ['AWS::ECS::', 'AWS::RDS::', 'AWS::EC2::', 'AWS::Lambda::Function'])
    expect(types.filter((t) => t.startsWith(forbidden))).toEqual([]);
});
