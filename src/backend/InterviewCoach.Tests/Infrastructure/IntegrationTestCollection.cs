namespace InterviewCoach.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "IntegrationTests";
}
