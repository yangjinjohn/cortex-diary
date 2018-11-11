﻿//The MIT License(MIT)

//Copyright(c) 2016 Chris Condron

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.
// https://github.com/condron/ReactiveUI-TreeView
// Modifications copyright(C) 2018 ei8/Elmer Bool

using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using ReactiveUI;
using Splat;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using works.ei8.Cortex.Diary.Application.Neurons;
using works.ei8.Cortex.Diary.Domain.Model.Neurons;
using works.ei8.Cortex.Diary.Domain.Model.Origin;
using works.ei8.Cortex.Diary.Port.Adapter.UI.ViewModels.Dialogs;

namespace works.ei8.Cortex.Diary.Port.Adapter.UI.ViewModels.Neurons
{
    public abstract class NeuronViewModelBase : ReactiveObject, IDisposable, IEquatable<NeuronViewModelBase>
    {
        private readonly string avatarUrl;
        private readonly IDisposable cleanUp;
        private int id;
        private string neuronId;
        private string data;
        private bool isExpanded;
        private bool isSelected;
        private ReadOnlyObservableCollection<NeuronViewModelBase> children;
        private IExtendedSelectionService selectionService;
        private readonly INeuronService neuronService;
        private readonly INeuronApplicationService neuronApplicationService;
        private readonly INeuronQueryService neuronQueryService;
        private readonly IOriginService originService;
        private readonly IStatusService statusService;
        private readonly IDialogService dialogService;
        private bool settingNeuron;
        // cortex graph poll interval + 100 millisecond allowance
        private const int ReloadDelay = 2100; 

        protected NeuronViewModelBase(string avatarUrl, Node<Neuron, int> node, SourceCache<Neuron, int> cache, NeuronViewModelBase parent = null, INeuronService neuronService = null, INeuronApplicationService neuronApplicationService = null, INeuronQueryService neuronQueryService = null, IOriginService originService = null, IExtendedSelectionService selectionService = null, IStatusService statusService = null, IDialogService dialogService = null)
        {
            this.avatarUrl = avatarUrl;
            this.Id = node.Key;
            this.Parent = parent;
            this.SetNeuron(node.Item);

            this.ReloadCommand = ReactiveCommand.Create(async() => await this.OnReload(cache));
            this.AddPostsynapticCommand = ReactiveCommand.Create<object>(
                async (parameter) => await this.OnAddPostsynaptic(cache, parameter)
            );
            this.AddPresynapticCommand = ReactiveCommand.Create(() => this.OnAddPresynaptic(cache));
            this.DeleteCommand = ReactiveCommand.Create(() => this.neuronService.Delete(cache, this.Neuron));

            this.neuronService = neuronService ?? Locator.Current.GetService<INeuronService>();
            this.neuronApplicationService = neuronApplicationService ?? Locator.Current.GetService<INeuronApplicationService>();
            this.neuronQueryService = neuronQueryService ?? Locator.Current.GetService<INeuronQueryService>();
            this.selectionService = selectionService ?? Locator.Current.GetService<IExtendedSelectionService>();
            this.originService = originService ?? Locator.Current.GetService<IOriginService>();
            this.statusService = statusService ?? Locator.Current.GetService<IStatusService>();
            this.dialogService = dialogService ?? Locator.Current.GetService<IDialogService>();

            var childrenLoader = new Lazy<IDisposable>(() => node.Children.Connect()
                .Transform(e =>
                    e.Item.Type == RelativeType.Postsynaptic ?
                    (NeuronViewModelBase)(new PostsynapticViewModel(avatarUrl, e.Item.Data, e, cache, this)) :
                    (NeuronViewModelBase)(new PresynapticViewModel(avatarUrl, e.Item.Data, e, cache, this)))
                .Bind(out this.children)
                .DisposeMany()
                .Subscribe()
                );

            var shouldExpand = node.IsRoot ?
                Observable.Return(true) :
                Parent.Value.WhenValueChanged(t => t.IsExpanded);

            var expander = shouldExpand
                .Where(isExpanded => isExpanded)
                .Take(1)
                .Subscribe(_ =>
                {
                    var x = childrenLoader.Value;
                });

            var childrenCount = node.Children.CountChanged
                .Select(count =>
                {
                    if (count == 0)
                        return "0 Synapses";
                    else
                        return $"{node.Children.Items.Count(n => n.Item.Type == RelativeType.Postsynaptic)} Postsynaptic; " +
                            $"{node.Children.Items.Count(n => n.Item.Type == RelativeType.Presynaptic)} Presynaptic";
                })
                .Subscribe(text => this.ChildrenCountText = text);

            var changeData = this.WhenPropertyChanged(p => p.Data, false)
                .Subscribe(x => 
                {
                    if (!this.settingNeuron)
                        this.neuronService.ChangeData(cache, this.Neuron, x.Value);
                }
                );

            var selector = this.WhenPropertyChanged(p => p.IsSelected)
                .Where(p => p.Value)
                .Subscribe(x => this.selectionService.SetSelectedComponents(new object[] { x.Sender }));

            this.cleanUp = Disposable.Create(() =>
            {
                expander.Dispose();
                childrenCount.Dispose();
                if (childrenLoader.IsValueCreated)
                    childrenLoader.Value.Dispose();
                selector.Dispose();
            });
        }

        private void SetNeuron(Neuron neuron)
        {
            this.settingNeuron = true;
            this.Neuron = neuron;
            this.NeuronId = neuron.NeuronId;
            this.Data = neuron.Data;
            this.settingNeuron = false;
        }

        private async Task OnReload(SourceCache<Neuron, int> cache, int millisecondsDelay = 0)
        {
            if (millisecondsDelay > 0)
                await Task.Delay(millisecondsDelay);

            await Helper.SetStatusOnComplete(async () =>
            {
                // reload self
                var reloadedNeuron = (await this.neuronQueryService.GetNeuronById(
                    this.avatarUrl,
                    this.Neuron.NeuronId,
                    this.Parent.ConvertOr(n => n.Neuron, () => null),
                    this.Neuron.Type
                    )).First();
                NeuronViewModelBase.CopyNeuronData(this.Neuron, reloadedNeuron);
                this.SetNeuron(this.Neuron);

                // reload relatives
                cache.Remove(cache.Items.Where(i => i.CentralId == this.Neuron.Id));
                var relatives = await this.neuronQueryService.GetNeurons(this.avatarUrl, this.Neuron);
                cache.AddOrUpdate(relatives);
                return true;
            },
            "Neuron reloaded successfully.",
            this.statusService
            );
        }

        private static void CopyNeuronData(Neuron target, Neuron source)
        {
            target.Data = source.Data;
            target.Type = source.Type;
            target.Timestamp = source.Timestamp;
            target.Version = source.Version;
            target.Errors = source.Errors;
        }

        private Task OnAddPresynaptic(SourceCache<Neuron, int> cache)
        {
            return Helper.SetStatusOnComplete(async () =>
                {
                    await this.neuronApplicationService.CreateNeuron(
                        this.avatarUrl,
                        Guid.NewGuid().ToString(),
                        "New Presynaptic",
                        this.originService.GetAvatarByUrl(this.avatarUrl).AuthorId,
                        new Terminal[] { NeuronViewModelBase.TempCreateTerminal(this.Neuron.NeuronId) }
                        );
                    await this.OnReload(cache, NeuronViewModelBase.ReloadDelay);
                    return true;
                },
                "Presynaptic added successfully.",
                this.statusService
            );
        }

        private async Task OnAddPostsynaptic(SourceCache<Neuron, int> cache, object parameter)
        {
            await Helper.SetStatusOnComplete(async () =>
                {
                    bool stat = false;
                    if (this.dialogService.ShowDialogSelectNeuron("Select Neuron", this.avatarUrl, parameter, out Neuron result).GetValueOrDefault())
                    {
                        await this.neuronApplicationService.AddTerminalsToNeuron(
                            this.avatarUrl,
                            this.neuronId,
                            this.originService.GetAvatarByUrl(this.avatarUrl).AuthorId,
                            new Terminal[] { NeuronViewModelBase.TempCreateTerminal(result.NeuronId) },
                            this.Neuron.Version
                            );
                        await this.OnReload(cache, NeuronViewModelBase.ReloadDelay);
                        stat = true;
                    }
                    return stat;
                },
                "Postsynaptic added successfully.",
                this.statusService,
                "Postsynaptic selection cancelled."
            );
        }

        // TODO: make Effect and Strength user-specified
        private static Terminal TempCreateTerminal(string targetId)
        {
            return new Terminal { TargetId = targetId, Effect = NeurotransmitterEffect.Excite, Strength = 1f };
        }

        public Neuron Neuron { get; private set; }

        private string childrenCountText;

        public string ChildrenCountText
        {
            get => this.childrenCountText;
            set => this.RaiseAndSetIfChanged(ref this.childrenCountText, value);
        }

        public int Id
        {
            get => this.id;
            set => this.RaiseAndSetIfChanged(ref this.id, value);
        }
                
        [ParenthesizePropertyName(true)]
        public string NeuronId
        {
            get => this.neuronId;
            set => this.RaiseAndSetIfChanged(ref this.neuronId, value);
        }
                
        public string Data
        {
            get => this.data;
            set => this.RaiseAndSetIfChanged(ref this.data, value);
        }

        public bool IsExpanded
        {
            get => this.isExpanded;
            set => this.RaiseAndSetIfChanged(ref isExpanded, value);
        }

        public bool IsSelected
        {
            get => this.isSelected;
            set => this.RaiseAndSetIfChanged(ref isSelected, value);
        }

        public ReactiveCommand ReloadCommand { get; }
        
        public ReactiveCommand AddPostsynapticCommand { get; }

        public ReactiveCommand AddPresynapticCommand { get; }

        public ReactiveCommand DeleteCommand { get; }

        public abstract object ViewModel { get; }

        public ReadOnlyObservableCollection<NeuronViewModelBase> Children => this.children;

        public Optional<NeuronViewModelBase> Parent { get; }

        public override string ToString()
        {
            return $"{this.NeuronId}:Neuron '{this.Data}'";
        }

        public void Dispose()
        {
            this.cleanUp.Dispose();
        }

        #region Equality Members

        public bool Equals(NeuronViewModelBase other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((NeuronViewModelBase) obj);
        }

        public override int GetHashCode()
        {
            return this.Id.GetHashCode();
        }

        public static bool operator ==(NeuronViewModelBase left, NeuronViewModelBase right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(NeuronViewModelBase left, NeuronViewModelBase right)
        {
            return !Equals(left, right);
        }

        #endregion
    }
}
