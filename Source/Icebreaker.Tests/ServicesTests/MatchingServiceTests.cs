﻿// <copyright file="MatchingServiceTests.cs" company="Microsoft">
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// </copyright>

namespace Icebreaker.Tests.ServicesTests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Icebreaker.Helpers;
    using Icebreaker.Interfaces;
    using Icebreaker.Services;
    using Microsoft.ApplicationInsights;
    using Microsoft.Bot.Builder.Adapters;
    using Microsoft.Bot.Connector;
    using Microsoft.Bot.Connector.Authentication;
    using Microsoft.Bot.Schema;
    using Microsoft.Bot.Schema.Teams;
    using Microsoft.Extensions.Logging;
    using Moq;
    using Xunit;

    /// <summary>
    /// Unit tests for the <see cref="MatchingService"/> class.
    /// </summary>
    public class MatchingServiceTests
    {
        private readonly IMatchingService sut;
        private readonly TestAdapter botAdapter;
        private readonly Mock<IBotDataProvider> dataProvider;
        private readonly Mock<ConversationHelper> conversationHelper;
        private readonly Mock<ISecretsProvider> secretsProvider;
        private readonly string apiKey;
        private readonly Mock<IAppSettings> appSettings;

        /// <summary>
        /// Initializes a new instance of the <see cref="MatchingServiceTests"/> class.
        /// </summary>
        public MatchingServiceTests()
        {
            this.botAdapter = new TestAdapter(Channels.Msteams)
            {
                Conversation =
                {
                    Conversation = new ConversationAccount
                    {
                        ConversationType = "channel",
                    },
                },
            };
            var telemetryClient = new TelemetryClient();
            this.appSettings = new Mock<IAppSettings>();
            this.appSettings.Setup(x => x.BotDisplayName).Returns(() => "Icebreaker");
            this.appSettings.Setup(x => x.DisableTenantFilter).Returns(() => true);
            this.appSettings.Setup(x => x.AllowedTenantIds).Returns(() => new HashSet<string>());

            this.secretsProvider = new Mock<ISecretsProvider>();
            this.secretsProvider.Setup(x => x.GetAppCredentialsAsync()).Returns(() => Task.FromResult(
                new MicrosoftAppCredentials(string.Empty, string.Empty) as AppCredentials));

            var conversationHelperLogger = new Mock<ILogger<ConversationHelper>>();
            this.conversationHelper = new Mock<ConversationHelper>(this.appSettings.Object, this.secretsProvider.Object, telemetryClient, conversationHelperLogger.Object);
            this.dataProvider = new Mock<IBotDataProvider>();
            this.dataProvider.Setup(x => x.GetInstalledTeamAsync(It.IsAny<string>()))
                .Returns(() => Task.FromResult(new TeamInstallInfo()));

            this.appSettings = new Mock<IAppSettings>();
            this.appSettings.Setup(x => x.MaxPairUpsPerTeam).Returns(() => 5000);
            this.appSettings.Setup(x => x.BotDisplayName).Returns(() => "IceBreakerServicePrincipal");
            this.apiKey = Guid.NewGuid().ToString();
            var secretsProvider = new Mock<ISecretsProvider>();
            secretsProvider.Setup(x => x.GetLogicAppKey()).Returns(this.apiKey);
            this.sut = new MatchingService(this.dataProvider.Object, this.conversationHelper.Object, telemetryClient, this.botAdapter, this.appSettings.Object);
        }

        [Fact]
        public async Task MatchPairs_NoTeamsInstalled_NoPairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult((IList<TeamInstallInfo>)new List<TeamInstallInfo>()));

            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>()));

            // Act
            // Send the message activity to the bot.
            var pairsNotifiedCount = await this.sut.MakePairsAndNotifyAsync();

            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // No call to GetTeamNameByIdAsync since no match
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Never);

            // No call to GetTeamMembers since no match
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Never);

            // No groups paired since no teams installed
            Assert.Equal(0, pairsNotifiedCount);
        }

        [Fact]
        public async Task MatchPairs_OneMemberOnlyExist_NoPairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult((IList<TeamInstallInfo>)new List<TeamInstallInfo>
                {
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                }));

            // No user opted-out
            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>()));

            // 1 member exist in team
            this.conversationHelper.Setup(x => x.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()))
                .Returns(() => Task.FromResult((IList<ChannelAccount>)new List<ChannelAccount>
                {
                    new ChannelAccount(Guid.NewGuid().ToString()),
                }));

            // Act
            // Send the message activity to the bot.
            var pairsNotifiedCount = await this.sut.MakePairsAndNotifyAsync();

            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // 2 calls to GetTeamNameByIdAsync since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 calls to GetTeamMembers since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // No groups paired since only 1 member exist in a team
            Assert.Equal(0, pairsNotifiedCount);
        }

        [Fact]
        public async Task MatchPairs_MultipleMembersExist_PairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult<IList<TeamInstallInfo>>(new List<TeamInstallInfo>
                {
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                }));

            // No user opted-out
            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>()));

            // 2 members exist in each team
            this.conversationHelper.Setup(x => x.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()))
                .Returns(() => Task.FromResult<IList<ChannelAccount>>(new List<ChannelAccount>
                {
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                }));

            // Act
            // Send the message activity to the bot.
            var pairsNotifiedCount = await this.sut.MakePairsAndNotifyAsync();

            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // 2 calls to GetTeamNameByIdAsync since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 calls to GetTeamMembers since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 groups are paired (1 group per team)
            Assert.Equal(2, pairsNotifiedCount);
        }

        [Fact]
        public async Task MatchPairs_MemberOptedOut_NoPairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult<IList<TeamInstallInfo>>(new List<TeamInstallInfo>
                {
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                }));

            var optedOutUserId = Guid.NewGuid().ToString();

            // No user opted-out
            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>
                {
                    { optedOutUserId, false },
                }));

            // 2 members exist in each team
            this.conversationHelper.Setup(x => x.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()))
                .Returns(() => Task.FromResult<IList<ChannelAccount>>(new List<ChannelAccount>
                {
                    new TeamsChannelAccount
                    {
                        Id = optedOutUserId,
                        AadObjectId = optedOutUserId,
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                }));

            // Act
            // Send the message activity to the bot.
            var pairsNotifiedCount = await this.sut.MakePairsAndNotifyAsync();

            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // 2 calls to GetTeamNameByIdAsync since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 calls to GetTeamMembers since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // No groups paired since only 1 member opted-in
            Assert.Equal(0, pairsNotifiedCount);
        }

        [Fact]
        public async Task MatchPairs_MembersOptedIn_PairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult<IList<TeamInstallInfo>>(new List<TeamInstallInfo>
                {
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                }));

            var optedOutUserId = Guid.NewGuid().ToString();

            // No user opted-out
            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>
                {
                    { optedOutUserId, true },
                }));

            // 2 members exist in each team
            this.conversationHelper.Setup(x => x.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()))
                .Returns(() => Task.FromResult((IList<ChannelAccount>)new List<ChannelAccount>
                {
                    new TeamsChannelAccount
                    {
                        Id = optedOutUserId,
                        AadObjectId = optedOutUserId,
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                }));

            // Act
            // Send the message activity to the bot.
            var pairsNotifiedCount = await this.sut.MakePairsAndNotifyAsync();

            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // 2 calls to GetTeamNameByIdAsync since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 calls to GetTeamMembers since we have 2 teams
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Exactly(2));

            // 2 groups are paired (1 group per team)
            Assert.Equal(2, pairsNotifiedCount);
        }

        [Fact]
        public async Task MatchPairs_MaxPairSettingIsZero_NoPairsGenerated()
        {
            // Arrange
            this.dataProvider.Setup(x => x.GetInstalledTeamsAsync())
                .Returns(() => Task.FromResult<IList<TeamInstallInfo>>(new List<TeamInstallInfo>
                {
                    new TeamInstallInfo { TeamId = Guid.NewGuid().ToString() },
                }));

            // No user opted-out
            this.dataProvider.Setup(x => x.GetAllUsersOptInStatusAsync())
                .Returns(() => Task.FromResult(new Dictionary<string, bool>()));

            // 2 members exist in each team
            this.conversationHelper.Setup(x => x.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()))
                .Returns(() => Task.FromResult<IList<ChannelAccount>>(new List<ChannelAccount>
                {
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                    new TeamsChannelAccount
                    {
                        Id = Guid.NewGuid().ToString(),
                        AadObjectId = Guid.NewGuid().ToString(),
                        UserPrincipalName = string.Empty,
                        Email = string.Empty,
                    },
                }));

            var currAppSettings = this.appSettings;
            currAppSettings.Setup(x => x.MaxPairUpsPerTeam).Returns(() => 0);

            var sut = new MatchingService(this.dataProvider.Object, this.conversationHelper.Object, new TelemetryClient(), this.botAdapter, this.appSettings.Object);

            // Act

            // Send the message activity to the bot.
            var pairsNotifiedCount = await sut.MakePairsAndNotifyAsync();
            // Assert GetInstalledTeamsAsync is called once
            this.dataProvider.Verify(m => m.GetInstalledTeamsAsync(), Times.Once);

            // Assert GetAllUsersOptInStatusAsync is called once
            this.dataProvider.Verify(m => m.GetAllUsersOptInStatusAsync(), Times.Once);

            // 1 calls to GetTeamNameByIdAsync since we have 1 team
            this.conversationHelper.Verify(m => m.GetTeamNameByIdAsync(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Once);

            // 1 calls to GetTeamMembers since we have 1 team
            this.conversationHelper.Verify(m => m.GetTeamMembers(this.botAdapter, It.IsAny<TeamInstallInfo>()), Times.Once);

            // No pairs since max limit is reached
            Assert.Equal(0, pairsNotifiedCount);
        }
    }
}