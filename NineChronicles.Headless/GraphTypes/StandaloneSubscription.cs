using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTAI;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Subscription;
using GraphQL.Types;
using Lib9c.Renderer;
using Libplanet;
using Libplanet.Blockchain;
using Libplanet.Net;
using Libplanet.Headless;
using Nekoyume.Action;
using Log = Serilog.Log;

namespace NineChronicles.Headless.GraphTypes
{
    public class StandaloneSubscription : ObjectGraphType
    {
        class TipChanged : ObjectGraphType<TipChanged>
        {
            public long Index { get; set; }

            public HashDigest<SHA256> Hash { get; set; }

            public TipChanged()
            {
                Field<NonNullGraphType<LongGraphType>>(nameof(Index));
                Field<ByteStringType>("hash", resolve: context => context.Source.Hash.ToByteArray());
            }
        }

        class PreloadStateType : ObjectGraphType<PreloadState>
        {
            private class PreloadStateExtra
            {
                public string Type { get; set; }
                public long CurrentCount { get; set; }
                public long TotalCount { get; set; }
            }

            private class PreloadStateExtraType : ObjectGraphType<PreloadStateExtra>
            {
                public PreloadStateExtraType()
                {
                    Field<NonNullGraphType<StringGraphType>>(nameof(PreloadStateExtra.Type));
                    Field<NonNullGraphType<LongGraphType>>(nameof(PreloadStateExtra.CurrentCount));
                    Field<NonNullGraphType<LongGraphType>>(nameof(PreloadStateExtra.TotalCount));
                }
            }

            public PreloadStateType()
            {
                Field<NonNullGraphType<LongGraphType>>(name: "currentPhase", resolve: context => context.Source.CurrentPhase);
                Field<NonNullGraphType<LongGraphType>>(name: "totalPhase", resolve: context => PreloadState.TotalPhase);
                Field<NonNullGraphType<PreloadStateExtraType>>(name: "extra", resolve: context =>
                {
                    var preloadState = context.Source;
                    return preloadState switch
                    {
                        ActionExecutionState actionExecutionState => new PreloadStateExtra
                        {
                            Type = nameof(ActionExecutionState),
                            CurrentCount = actionExecutionState.ExecutedBlockCount,
                            TotalCount = actionExecutionState.TotalBlockCount,
                        },
                        BlockDownloadState blockDownloadState => new PreloadStateExtra
                        {
                            Type = nameof(BlockDownloadState),
                            CurrentCount = blockDownloadState.ReceivedBlockCount,
                            TotalCount = blockDownloadState.TotalBlockCount,
                        },
                        BlockHashDownloadState blockHashDownloadState => new PreloadStateExtra
                        {
                            Type = nameof(BlockHashDownloadState),
                            CurrentCount = blockHashDownloadState.ReceivedBlockHashCount,
                            TotalCount = blockHashDownloadState.EstimatedTotalBlockHashCount,
                        },
                        BlockVerificationState blockVerificationState => new PreloadStateExtra
                        {
                            Type = nameof(BlockVerificationState),
                            CurrentCount = blockVerificationState.VerifiedBlockCount,
                            TotalCount = blockVerificationState.TotalBlockCount,
                        },
                        StateDownloadState stateDownloadState => new PreloadStateExtra
                        {
                            Type = nameof(StateDownloadState),
                            CurrentCount = stateDownloadState.ReceivedIterationCount,
                            TotalCount = stateDownloadState.TotalIterationCount,
                        },
                        _ => throw new ExecutionError($"Not supported preload state. {preloadState.GetType()}"),
                    };
                });
            }
        }

        private ISubject<TipChanged> _subject = new ReplaySubject<TipChanged>();

        private StandaloneContext StandaloneContext { get; }

        public StandaloneSubscription(StandaloneContext standaloneContext)
        {
            StandaloneContext = standaloneContext;
            AddField(new EventStreamFieldType {
                Name = "tipChanged",
                Type = typeof(TipChanged),
                Resolver = new FuncFieldResolver<TipChanged>(ResolveTipChanged),
                Subscriber = new EventStreamResolver<TipChanged>(SubscribeTipChanged),
            });
            AddField(new EventStreamFieldType {
                Name = "preloadProgress",
                Type = typeof(PreloadStateType),
                Resolver = new FuncFieldResolver<PreloadState>(context => context.Source as PreloadState),
                Subscriber = new EventStreamResolver<PreloadState>(context => StandaloneContext.PreloadStateSubject.AsObservable()),
            });
            AddField(new EventStreamFieldType
            {
                Name = "nodeStatus",
                Type = typeof(NodeStatusType),
                Resolver = new FuncFieldResolver<NodeStatusType>(context => context.Source as NodeStatusType),
                Subscriber = new EventStreamResolver<NodeStatusType>(context => StandaloneContext.NodeStatusSubject.AsObservable()),
            });
            AddField(new EventStreamFieldType
            {
                Name = "differentAppProtocolVersionEncounter",
                Type = typeof(NonNullGraphType<DifferentAppProtocolVersionEncounterType>),
                Resolver = new FuncFieldResolver<DifferentAppProtocolVersionEncounter>(context =>
                    (DifferentAppProtocolVersionEncounter)context.Source),
                Subscriber = new EventStreamResolver<DifferentAppProtocolVersionEncounter>(context =>
                    StandaloneContext.DifferentAppProtocolVersionEncounterSubject.AsObservable()),
            });
            AddField(new EventStreamFieldType
            {
                Name = "notification",
                Type = typeof(NonNullGraphType<NotificationType>),
                Resolver = new FuncFieldResolver<Notification>(context => context.Source as Notification),
                Subscriber = new EventStreamResolver<Notification>(context =>
                    StandaloneContext.NotificationSubject.AsObservable()),
            });
            AddField(new EventStreamFieldType
            {
                Name = "nodeException",
                Type = typeof(NonNullGraphType<NodeExceptionType>),
                Resolver = new FuncFieldResolver<NodeException>(context => context.Source as NodeException),
                Subscriber = new EventStreamResolver<NodeException>(context =>
                    StandaloneContext.NodeExceptionSubject.AsObservable()),
            });
        }

        public void RegisterTipChangedSubscription()
        {
            BlockRenderer blockRenderer = StandaloneContext?.NineChroniclesNodeService?.BlockRenderer ??
                StandaloneContext?.BlockChain.Renderers.OfType<BlockRenderer>().FirstOrDefault();

            if (blockRenderer is null)
            {
                throw new InvalidOperationException(
                    $"Failed to find {nameof(ActionRenderer)} instance; before calling " +
                    $"{nameof(RegisterTipChangedSubscription)}(), {nameof(StandaloneContext)}." +
                    $"{nameof(StandaloneContext.NineChroniclesNodeService)} has to be set, or " +
                    $"{nameof(StandaloneContext)}.{nameof(StandaloneContext.BlockChain)}." +
                    $"{nameof(StandaloneContext.BlockChain.Renderers)} have to contain an " +
                    $"{nameof(ActionRenderer)} instance."
                );
            }

            blockRenderer.EveryBlock()
                .Subscribe(pair =>
                    _subject.OnNext(new TipChanged
                    {
                        Index = pair.NewTip.Index,
                        Hash = pair.NewTip.Hash,
                    })
                );
        }

        private TipChanged ResolveTipChanged(IResolveFieldContext context)
        {
            return context.Source as TipChanged;
        }

        private IObservable<TipChanged> SubscribeTipChanged(IResolveEventStreamContext context)
        {
            return _subject.AsObservable();
        }
    }
}
