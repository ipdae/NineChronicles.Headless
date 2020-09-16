using Bencodex.Types;
using GraphQL;
using GraphQL.Types;
using Libplanet;
using Libplanet.Assets;
using Libplanet.Blockchain;
using Libplanet.Crypto;
using Nekoyume;
using Nekoyume.Action;
using Nekoyume.Model.State;
using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

namespace NineChronicles.Standalone.GraphTypes
{
    public class GoldMutation : ObjectGraphType<NineChroniclesNodeService>
    {
        public GoldMutation()
        {
            Field<NonNullGraphType<BooleanGraphType>>(
                "transfer",
                arguments: new QueryArguments(
                    new QueryArgument<NonNullGraphType<AddressType>>
                    {
                        Name = "recipient",
                    },
                    new QueryArgument<NonNullGraphType<StringGraphType>>
                    {
                        Name = "amount"
                    }
                ),
                resolve: context =>
                {
                    NineChroniclesNodeService service = context.Source;
                    PrivateKey privateKey = service.PrivateKey;
                    BlockChain<NCAction> blockChain = service.Swarm.BlockChain;
                    var currency = CurrencyExtensions.Deserialize(
                        (Dictionary)blockChain.GetState(GoldCurrencyState.Address)
                    );
                    FungibleAssetValue amount =
                    FungibleAssetValue.Parse(currency, context.GetArgument<string>("amount"));

                    Address recipient = context.GetArgument<Address>("recipient");
                    
                    blockChain.MakeTransaction(
                        privateKey,
                        new NCAction[]
                        {
                            new TransferAsset(
                                privateKey.ToAddress(),
                                recipient,
                                amount
                            ),
                        }
                    );
                    return true;
                }
            );
        }
    }
}
