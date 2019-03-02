﻿using System;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using TangramCypher.ApplicationLayer.Wallet;
using TangramCypher.Helper;

namespace TangramCypher.ApplicationLayer.Commands.Wallet
{
    [CommandDescriptor(new string[] { "wallet", "balance" }, "Get current wallet balance")]
    public class WalletBalanceCommand : Command
    {
        readonly IWalletService walletService;
        readonly IConsole console;

        public WalletBalanceCommand(IServiceProvider serviceProvider)
        {
            walletService = serviceProvider.GetService<IWalletService>();
            console = serviceProvider.GetService<IConsole>();
        }

        public override async Task Execute()
        {
            try
            {
                var identifier = Prompt.GetPassword("Identifier:", ConsoleColor.Yellow).ToSecureString();
                var password = Prompt.GetPassword("Password:", ConsoleColor.Yellow).ToSecureString();

                var total = await walletService.AvailableBalance(identifier, password);

                console.ForegroundColor = ConsoleColor.Magenta;
                console.WriteLine($"\nWallet balance: {total}\n");
                console.ForegroundColor = ConsoleColor.White;

            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}
