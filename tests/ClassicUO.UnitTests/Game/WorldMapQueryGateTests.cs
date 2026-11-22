using System.Reflection;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// The 0xF0 guild/party world-map query is no longer gated on socket encryption or on the OSI new-movement feature bit
    /// (0x4000): ModernUO and ServUO answer it with encryption on, and this client has no OSI movement path. The 0xF1/0xF2
    /// ids (OSI time sync) are unknown to the packet table.
    /// </summary>
    [Collection("PlayerStateGlobals")]
    public class WorldMapQueryGateTests
    {
        private const uint OldNewMovementBit = 0x4000;

        private static readonly PropertyInfo ProfileProp = typeof(ProfileManager).GetProperty(nameof(ProfileManager.CurrentProfile))!;
        private static readonly PropertyInfo EncryptionProp = typeof(NetClient).GetProperty(nameof(NetClient.Encryption))!;

        private static bool EnabledWith(bool encrypted, CharacterListFlags flags)
        {
            Profile oldProfile = ProfileManager.CurrentProfile;
            EncryptionHelper oldEncryption = NetClient.Socket.Encryption;
            bool counted = false;

            try
            {
                var world = new World();
                world.ClientFeatures.SetFlags(flags);
                ProfileProp.SetValue(null, new Profile { WorldMapShowParty = true });

                EncryptionHelper helper = new EncryptionHelper(ClientVersion.CV_70130);

                if (encrypted)
                {
                    helper.EncryptionType.Should().NotBe(EncryptionType.NONE);
                }

                EncryptionProp.SetValue(NetClient.Socket, encrypted ? helper : null);
                WorldMapGump.NoteOpened(ref counted);

                return world.WMapManager.Enabled;
            }
            finally
            {
                WorldMapGump.NoteClosed(ref counted);
                EncryptionProp.SetValue(NetClient.Socket, oldEncryption);
                ProfileProp.SetValue(null, oldProfile);
            }
        }

        [Theory]
        [InlineData(false, 0u)]
        [InlineData(true, 0u)]
        [InlineData(false, OldNewMovementBit)]
        [InlineData(true, OldNewMovementBit)]
        public void Query_Is_Enabled_With_Encryption_And_The_Old_Feature_Bit(bool encrypted, uint flags)
        {
            EnabledWith(encrypted, (CharacterListFlags)flags).Should().BeTrue();
        }

        [Fact]
        public void Packet_Table_Has_No_Entries_For_The_Removed_Ids()
        {
            foreach (ClientVersion version in new[] { ClientVersion.CV_70130, ClientVersion.CV_6060, ClientVersion.CV_7090 })
            {
                var table = new PacketsTable(version);

                table.GetPacketLength(0xF1).Should().Be(-1, $"0xF1 at {version}");
                table.GetPacketLength(0xF2).Should().Be(-1, $"0xF2 at {version}");
                table.GetPacketLength(0xF3).Should().BeGreaterThan(0, $"0xF3 (item update) is kept at {version}");
            }
        }
    }
}
