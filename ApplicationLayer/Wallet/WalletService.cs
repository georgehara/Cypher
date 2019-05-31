// Cypher (c) by Tangram Inc
// 
// Cypher is licensed under a
// Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
// 
// You should have received a copy of the license along with this
// work. If not, see <http://creativecommons.org/licenses/by-nc-nd/4.0/>.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using MurrayGrant.ReadablePassphrase;
using Newtonsoft.Json.Linq;
using SimpleBase;
using TangramCypher.ApplicationLayer.Vault;
using TangramCypher.Helper;
using TangramCypher.Helper.LibSodium;
using TangramCypher.ApplicationLayer.Coin;
using TangramCypher.ApplicationLayer.Actor;
using System.Text;
using TangramCypher.ApplicationLayer.Helper.ZeroKP;
using Microsoft.Extensions.Configuration;
using Dawn;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TangramCypher.Model;

namespace TangramCypher.ApplicationLayer.Wallet
{
    public class WalletService : IWalletService
    {
        private readonly IVaultServiceClient vaultServiceClient;
        private readonly IConfigurationSection apiNetworkSection;
        private readonly ILogger logger;
        private readonly string environment;
        private readonly IUnitOfWork unitOfWork;

        public WalletService(IVaultServiceClient vaultServiceClient, IConfiguration configuration, ILogger logger, IUnitOfWork unitOfWork)
        {
            this.vaultServiceClient = vaultServiceClient;

            apiNetworkSection = configuration.GetSection(Constant.ApiNetwork);
            environment = apiNetworkSection.GetValue<string>(Constant.Environment);

            this.logger = logger;

            this.unitOfWork = unitOfWork;
        }

        /// <summary>
        /// Gets the generic available balance.
        /// </summary>
        /// <returns>The balance.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        public async Task<ulong> AvailableBalance(SecureString identifier, SecureString password)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            var transactions = await Transactions(identifier, password);

            //Test...
            var txns = await unitOfWork.GetTransactionRepository().All(identifier, password, "transactions");

            return Balance(identifier, password, transactions);
        }

        /// <summary>
        /// Creates new secret/public address key.
        /// </summary>
        /// <returns>The pk sk.</returns>
        public PkSkDto CreatePkSk()
        {
            var kp = Cryptography.KeyPair();

            return new PkSkDto()
            {
                PublicKey = kp.PublicKey.ToHex(),
                SecretKey = kp.SecretKey.ToHex(),
                Address = Encoding.UTF8.GetString(NetworkAddress(kp.PublicKey))
            };
        }

        /// <summary>
        /// Create new wallet.
        /// </summary>
        /// <returns>The wallet.</returns>
        public async Task<CredentialsDto> CreateWallet()
        {
            var walletId = NewID(16);
            var passphrase = Passphrase();
            var pkSk = CreatePkSk();

            walletId.MakeReadOnly();
            passphrase.MakeReadOnly();

            try
            {
                await vaultServiceClient.CreateUserAsync(walletId, passphrase);

                var dic = new Dictionary<string, object>
                {
                    { "storeKeys", new List<PkSkDto> { pkSk } }
                };

                await vaultServiceClient.SaveDataAsync(
                    walletId,
                    passphrase,
                            $"wallets/{walletId.ToUnSecureString()}/wallet",
                    dic);

                return new CredentialsDto { Identifier = walletId.ToUnSecureString(), Password = passphrase.ToUnSecureString() };
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message);
                throw new Exception("Failed to create wallet. Is the vault unsealed?");
            }
            finally
            {
                walletId.Dispose();
                passphrase.Dispose();
            }
        }

        /// <summary>
        /// Creates a new identifier.
        /// </summary>
        /// <returns>The identifier.</returns>
        /// <param name="bytes">Bytes.</param>
        public SecureString NewID(int bytes = 32)
        {
            var secureString = new SecureString();
            foreach (var c in $"id_{Cryptography.RandomBytes(bytes).ToHex()}") secureString.AppendChar(c);
            return secureString;
        }

        /// <summary>
        /// Creates a new passphrase.
        /// </summary>
        /// <returns>The passphrase.</returns>
        public SecureString Passphrase()
        {
            var defaultDict = MurrayGrant.ReadablePassphrase.Dictionaries.Default.Load();
            var easyCreatedGenerator = Generator.Create();
            return easyCreatedGenerator.GenerateAsSecure(PhraseStrength.RandomForever);
        }

        /// <summary>
        /// Hashs the password.
        /// </summary>
        /// <returns>The password.</returns>
        /// <param name="passphrase">Passphrase.</param>
        public byte[] HashPassword(SecureString passphrase) => Cryptography.ArgonHashPassword(passphrase);

        /// <summary>
        /// Adds message tracking.
        /// </summary>
        /// <returns>The message tracking.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="messageTrack">Message track.</param>
        public async Task<bool> AddMessageTracking(SecureString identifier, SecureString password, MessageTrackDto messageTrack)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(messageTrack, nameof(messageTrack)).NotNull();

            bool added = false;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var found = false;
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");

                    if (data.Data.TryGetValue("track", out object msgs))
                    {
                        foreach (JObject item in ((JArray)msgs).Children().ToList())
                        {
                            var pk = item.GetValue("PublicKey");
                            found = pk.Value<string>().Equals(messageTrack.PublicKey);
                        }

                        if (!found)
                            ((JArray)msgs).Add(JObject.FromObject(messageTrack));
                        else
                            ((JArray)msgs).Replace(JObject.FromObject(messageTrack));
                    }
                    else
                        data.Data.Add("track", new List<MessageTrackDto> { messageTrack });

                    await vaultServiceClient.SaveDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet", data.Data);

                    added = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return added;
        }

        public async Task<bool> Put<T>(SecureString identifier, SecureString password, string key, T value, string storeName, string keyName)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            bool added = false;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var found = false;
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");

                    if (data.Data.TryGetValue(storeName, out object txs))
                    {
                        foreach (JObject item in ((JArray)txs).Children().ToList())
                        {
                            var hash = item.GetValue(keyName);
                            found = hash.Value<string>().Equals(key);
                        }
                        if (!found)
                            ((JArray)txs).Add(JObject.FromObject(value));
                    }
                    else
                        data.Data.Add(storeName, new List<T> { value });

                    await vaultServiceClient.SaveDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet", data.Data);

                    added = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return added;
        }

        /// <summary>
        /// Gets the stored message track.
        /// </summary>
        /// <returns>The track.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="pk">Pk.</param>
        public async Task<MessageTrackDto> MessageTrack(SecureString identifier, SecureString password, string pk)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            MessageTrackDto messageTrack = null;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");
                    if (data.Data.TryGetValue("messages", out object msgs))
                    {
                        messageTrack = ((JArray)msgs).ToObject<List<MessageTrackDto>>().FirstOrDefault(msg => msg.PublicKey.Equals(pk));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return messageTrack;
        }

        /// <summary>
        /// Gets the transaction.
        /// </summary>
        /// <returns>The transaction.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="hash">Hash.</param>
        public async Task<TransactionDto> Transaction(SecureString identifier, SecureString password, string hash)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(hash, nameof(hash)).NotNull().NotEmpty();

            var transactions = await Transactions(identifier, password);

            if (transactions == null)
                return null;

            return transactions.FirstOrDefault(t => t.Hash.Equals(hash));
        }

        /// <summary>
        /// Gets the total transaction amount.
        /// </summary>
        /// <returns>The transaction amount.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="stamp">Stamp.</param>
        public async Task<ulong> TotalTransactionAmount(SecureString identifier, SecureString password, string stamp)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();

            var transactions = await Transactions(identifier, password);

            if (transactions == null)
                return 0;

            var total = Sum(transactions.Where(tx => tx.Stamp.Equals(stamp)).Select(p => p.Amount));

            return total;
        }

        /// <summary>
        /// Last transaction amount.
        /// </summary>
        /// <returns>The transaction amount.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        public async Task<TransactionDto> LastTransaction(SecureString identifier, SecureString password, TransactionType transactionType)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            var transactions = await Transactions(identifier, password);

            if (transactions == null)
                return null;

            var transaction = transactions.Last(tx => tx.TransactionType.Equals(transactionType));
            return transaction;
        }

        /// <summary>
        /// Gets the envelope.
        /// </summary>
        /// <returns>The envelope.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        public async Task<List<TransactionDto>> Transactions(SecureString identifier, SecureString password)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            List<TransactionDto> transactions = null;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");
                    if (data.Data.TryGetValue("transactions", out object txs))
                    {
                        transactions = ((JArray)txs).ToObject<List<TransactionDto>>();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return transactions;
        }

        /// <summary>
        /// Gets the store key.
        /// </summary>
        /// <returns>The store key.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="storeKey">Store key.</param>
        public async Task<SecureString> StoreKey(SecureString identifier, SecureString password, string storeKey)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(storeKey, nameof(storeKey)).NotNull().NotEmpty();

            var secureString = new SecureString();

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");
                    var storeKeys = JObject.FromObject(data.Data["storeKeys"]);
                    var key = storeKeys.GetValue(storeKey).Value<string>();

                    foreach (var c in key) secureString.AppendChar(Convert.ToChar(c));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                    throw ex;
                }
            }

            return secureString;
        }

        /// <summary>
        ///  Gets the store key from the address.
        /// </summary>
        /// <returns>The key.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="storeKeyApi">Store key API.</param>
        /// <param name="address">Address.</param>
        public async Task<SecureString> StoreKey(SecureString identifier, SecureString password, StoreKeyApiMethod storeKeyApi, string address)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(address, nameof(address)).NotNull().NotEmpty();

            SecureString secureString = null;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");

                    if (data.Data.TryGetValue("storeKeys", out object keys))
                    {
                        foreach (JObject item in ((JArray)keys).Children().ToList())
                        {
                            var addressKey = item.GetValue("Address");
                            if (addressKey.Value<string>().Equals(address))
                            {
                                var key = item.GetValue(storeKeyApi.ToString()).Value<string>();

                                secureString = new SecureString();

                                foreach (var c in key) secureString.AppendChar(Convert.ToChar(c));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return secureString;
        }

        /// <summary>
        /// Select random address.
        /// </summary>
        /// <returns>The address.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        public async Task<string> RandomAddress(SecureString identifier, SecureString password)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            string address = null;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");

                    if (data.Data.TryGetValue("storeKeys", out object keys))
                    {
                        var rnd = new Random();
                        var pkSks = ((JArray)keys).ToObject<List<PkSkDto>>();

                        address = pkSks[rnd.Next(pkSks.Count())].Address;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                    throw ex;
                }
            }

            return address;
        }

        /// <summary>
        /// Sorts the change.
        /// </summary>
        /// <returns>The change.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="amount">Amount.</param>
        public async Task<TransactionCoin> SortChange(SecureString identifier, SecureString password, ulong amount)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            var transactions = await Transactions(identifier, password);

            if (transactions == null)
                return null;

            TransactionCoin transactionCoin = null;
            TransactionDto[] txsIn = transactions.Where(tx => tx.TransactionType == TransactionType.Receive).OrderBy(tx => tx.Version).ToArray();
            TransactionDto[] target = new TransactionDto[txsIn.Length];

            Array.Copy(txsIn, target, txsIn.Length);
            for (int i = 0, targetLength = target.Length; i < targetLength; i++)
            {
                (TransactionDto transaction, double amountFor) = CalculateChange(amount, txsIn);
                var balance = Balance(identifier, password, transactions.Where(tx => tx.Stamp == transaction.Stamp).ToList());

                if (balance >= amountFor)
                {
                    transactionCoin = new TransactionCoin
                    {
                        Balance = balance,
                        Input = amount,
                        Output = balance - amount,
                        Stamp = transaction.Stamp
                    };

                    transactionCoin.Chain = transactions.Where(tx => tx.Stamp.Equals(transaction.Stamp)).ToList();
                    transactionCoin.Version = transactionCoin.Chain.Last().Version;

                    if (transactionCoin.Output.Equals(0))
                        transactionCoin.Spent = true;

                    break;
                }

                var idx = Array.FindIndex(txsIn, t => t.Stamp.Equals(transaction.Stamp));
                txsIn = txsIn.Where((source, index) => index != idx).ToArray();
            }

            return transactionCoin;
        }

        /// <summary>
        /// Wallet profile Profile.
        /// </summary>
        /// <returns>The profile.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        public async Task<string> Profile(SecureString identifier, SecureString password)
        {
            string profile = null;

            try
            {
                using (var id = identifier.Insecure())
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{id.Value}/wallet");
                    profile = JsonConvert.SerializeObject(data);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message);
                throw;
            }

            return profile;
        }

        /// <summary>
        /// Lists the wallets available.
        /// </summary>
        /// <returns>The identifier list.</returns>
        public async Task<IEnumerable<string>> WalletList()
        {
            var data = await vaultServiceClient.GetListAsync($"wallets/");
            var keys = data.Data?.Keys;
            return keys;
        }

        /// <summary>
        /// Calculate balance from transactions.
        /// </summary>
        /// <returns>The balance.</returns>
        /// <param name="identifier">Identifier.</param>
        /// <param name="password">Password.</param>
        /// <param name="transactions">Transactions.</param>
        private ulong Balance(SecureString identifier, SecureString password, List<TransactionDto> transactions)
        {
            var total = 0UL;

            if (transactions != null)
            {
                ulong? pocket = null;
                ulong? burnt = null;

                try
                {
                    pocket = Sum(transactions.Where(tx => tx.TransactionType == TransactionType.Receive).Select(p => p.Amount));
                    burnt = Sum(transactions.Where(tx => tx.TransactionType == TransactionType.Send).Select(p => p.Amount));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
                finally
                {
                    switch (burnt)
                    {
                        case null:
                            total = pocket == null ? 0 : pocket.Value;
                            break;
                        default:
                            {
                                total = pocket.Value - burnt.Value;
                                break;
                            }
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// Calculates the change.
        /// </summary>
        /// <returns>The change.</returns>
        /// <param name="amount">Amount.</param>
        /// <param name="transactions">Transactions.</param>
        private (TransactionDto, ulong) CalculateChange(ulong amount, TransactionDto[] transactions)
        {
            Guard.Argument(transactions, nameof(transactions)).NotNull();

            int count;
            var tempTxs = new List<TransactionDto>();

            for (var i = 0; i < transactions.Length; i++)
            {
                count = (int)(amount / transactions[i].Amount);
                if (count != 0)
                    for (int k = 0; k < count; k++) tempTxs.Add(transactions[i]);

                amount %= transactions[i].Amount;
            }

            var sum = Sum(tempTxs.Select(s => s.Amount));
            var remainder = amount - sum;
            var closest = transactions.Select(x => x.Amount).Aggregate((x, y) => x - remainder < y - remainder ? x : y);
            var tx = transactions.FirstOrDefault(a => a.Amount.Equals(closest));

            return (tx, remainder);
        }

        /// <summary>
        /// Network address.
        /// </summary>
        /// <returns>The address.</returns>
        /// <param name="coin">Coin.</param>
        /// <param name="networkApi">Network API.</param>
        public byte[] NetworkAddress(CoinDto coin, NetworkApiMethod networkApi = null)
        {
            Guard.Argument(coin, nameof(coin)).NotNull();

            //TODO: Will remove the need to format to and from base64..
            try
            { coin = coin.FormatCoinFromBase64(); }
            catch (FormatException) { }

            string env = string.Empty;
            byte[] address = new byte[33];

            env = networkApi == null ? environment : networkApi.ToString();
            address[0] = env == Constant.Mainnet ? (byte)0x1 : (byte)74;

            var hash = Cryptography.GenericHashWithKey(
                $"{coin.Envelope.Commitment}" +
                $" {coin.Envelope.Proof}" +
                $" {coin.Envelope.PublicKey}" +
                $" {coin.Envelope.Signature}" +
                $" {coin.Hash}" +
                $" {coin.Hint}" +
                $" {coin.Keeper}" +
                $" {coin.Principle}" +
                $" {coin.Stamp}" +
                $" {coin.Version}",
                coin.Principle.FromHex());

            Array.Copy(hash, 0, address, 1, 32);

            return Encoding.UTF8.GetBytes(Base58.Bitcoin.Encode(address));
        }

        /// <summary>
        /// Network address.
        /// </summary>
        /// <returns>The address.</returns>
        /// <param name="pk">Pk.</param>
        /// <param name="networkApi">Network API.</param>
        public byte[] NetworkAddress(byte[] pk, NetworkApiMethod networkApi = null)
        {
            Guard.Argument(pk, nameof(pk)).NotNull().MaxCount(32);

            string env = string.Empty;
            byte[] address = new byte[33];

            env = networkApi == null ? environment : networkApi.ToString();
            address[0] = env == Constant.Mainnet ? (byte)0x1 : (byte)74;

            Array.Copy(pk, 0, address, 1, 32);

            return Encoding.UTF8.GetBytes(Base58.Bitcoin.Encode(address));
        }

        /// <summary>
        /// Returns provers password.
        /// </summary>
        /// <returns>The password.</returns>
        /// <param name="password">Password.</param>
        /// <param name="version">Version.</param>
        public string ProverPassword(SecureString password, int version)
        {
            Guard.Argument(password, nameof(password)).NotNull();
            Guard.Argument(version, nameof(version)).NotNegative();

            using (var insecurePassword = password.Insecure())
            {
                var hash = Cryptography.GenericHashNoKey($"{version} {insecurePassword.Value}");
                return Prover.GetHashStringNumber(hash).ToByteArray().ToHex();
            }
        }

        public async Task<bool> ClearTransactions(SecureString identifier, SecureString password)
        {
            Guard.Argument(identifier, nameof(identifier)).NotNull();
            Guard.Argument(password, nameof(password)).NotNull();

            bool cleared = false;

            using (var insecureIdentifier = identifier.Insecure())
            {
                try
                {
                    var data = await vaultServiceClient.GetDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet");

                    if (data.Data.TryGetValue("transactions", out object txs))
                    {
                        data.Data.Add("transactions", new List<TransactionDto>());
                    }

                    await vaultServiceClient.SaveDataAsync(identifier, password, $"wallets/{insecureIdentifier.Value}/wallet", data.Data);

                    cleared = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                }
            }

            return cleared;
        }

        /// <summary>
        /// naT UInt64 format.
        /// </summary>
        /// <returns>The t.</returns>
        /// <param name="value">Value.</param>
        public ulong MulWithNaT(ulong value) => (ulong)(value * Constant.NanoTan);

        /// <summary>
        /// naT UInt64 format.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public ulong DivWithNaT(ulong value) => (ulong)(value / Constant.NanoTan);

        private static ulong Sum(IEnumerable<ulong> source)
        {
            var sum = 0UL;
            foreach (var number in source)
            {
                sum += number;
            }
            return sum;
        }
    }
}