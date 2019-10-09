using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Barracuda;
using MLAgents.InferenceBrain;
using UnityEngine.Profiling;

namespace MLAgents
{
    public enum InferenceDevice
    {
        CPU = 0,
        GPU = 1
    }

    /// <summary>
    /// The Learning Brain works differently if you are training it or not.
    /// When training your Agents, the LearningBrain will be controlled by Python.
    /// When using a pretrained model, just drag the Model file into the
    /// Model property of the Learning Brain and do not launch the Python training process.
    /// The training will start automatically if Python is ready to train and there is at
    /// least one LearningBrain in the scene.
    /// The property model corresponds to the Model currently attached to the Brain. Before
    /// being used, a call to ReloadModel is required.
    /// When the Learning Brain is not training, it uses a TensorFlow model to make decisions.
    /// The Proximal Policy Optimization (PPO) and Behavioral Cloning algorithms included with
    /// the ML-Agents SDK produce trained TensorFlow models that you can use with the
    /// Learning Brain.
    /// </summary>
    [CreateAssetMenu(fileName = "NewLearningBrain", menuName = "ML-Agents/Learning Brain")]
    public class LearningBrain : Brain
    {
        private ITensorAllocator m_TensorAllocator;
        private TensorGenerator m_TensorGenerator;
        private TensorApplier m_TensorApplier;
        public NNModel model;
        private Model m_BarracudaModel;
        private IWorker m_Engine;
        private bool m_Verbose = false;

        private BarracudaModelParamLoader m_ModelParamLoader;
        private string[] m_OutputNames;

        [Tooltip("Inference execution device. CPU is the fastest option for most of ML Agents models. " +
            "(This field is not applicable for training).")]
        public InferenceDevice inferenceDevice = InferenceDevice.CPU;

        private IReadOnlyList<TensorProxy> m_InferenceInputs;
        private IReadOnlyList<TensorProxy> m_InferenceOutputs;

        protected ICommunicator m_Communicator;

        /// <summary>
        /// Sets the Communicator of the Brain. The brain will call the communicator at every step and give
        /// it the agent's data using PutObservations at each DecideAction call.
        /// </summary>
        /// <param name="communicator"> The Batcher the brain will use for the current session</param>
        private void SetCommunicator(ICommunicator communicator)
        {
            m_Communicator = communicator;
            m_Communicator?.SubscribeBrain(name, brainParameters);
            LazyInitialize();

        }

        /// <inheritdoc />
        protected override void Initialize()
        {
            ReloadModel();
            var comm = FindObjectOfType<Academy>()?.Communicator;
            SetCommunicator(comm);
        }

        /// <summary>
        /// Initializes the Brain with the Model that it will use when selecting actions for
        /// the agents
        /// </summary>
        /// <param name="seed"> The seed that will be used to initialize the RandomNormal
        /// and Multinomial obsjects used when running inference.</param>
        /// <exception cref="UnityAgentsException">Throws an error when the model is null
        /// </exception>
        public void ReloadModel(int seed = 0)
        {
            if (m_TensorAllocator == null)
                m_TensorAllocator = new TensorCachingAllocator();

            if (model != null)
            {
#if BARRACUDA_VERBOSE
                _verbose = true;
#endif

                D.logEnabled = m_Verbose;

                // Cleanup previous instance
                if (m_Engine != null)
                    m_Engine.Dispose();

                m_BarracudaModel = ModelLoader.Load(model.Value);
                var executionDevice = inferenceDevice == InferenceDevice.GPU
                    ? BarracudaWorkerFactory.Type.ComputePrecompiled
                    : BarracudaWorkerFactory.Type.CSharp;

                m_Engine = BarracudaWorkerFactory.CreateWorker(executionDevice, m_BarracudaModel, m_Verbose);
            }
            else
            {
                m_BarracudaModel = null;
                m_Engine = null;
            }

            m_ModelParamLoader = BarracudaModelParamLoader.GetLoaderAndCheck(m_Engine, m_BarracudaModel, brainParameters);
            m_InferenceInputs = m_ModelParamLoader.GetInputTensors();
            m_OutputNames = m_ModelParamLoader.GetOutputNames();
            m_TensorGenerator = new TensorGenerator(brainParameters, seed, m_TensorAllocator, m_BarracudaModel);
            m_TensorApplier = new TensorApplier(brainParameters, seed, m_TensorAllocator, m_BarracudaModel);
        }

        /// <summary>
        /// Return a list of failed checks corresponding to the failed compatibility checks
        /// between the Model and the BrainParameters. Note : This does not reload the model.
        /// If changes have been made to the BrainParameters or the Model, the model must be
        /// reloaded using GiveModel before trying to get the compatibility checks.
        /// </summary>
        /// <returns> The list of the failed compatibility checks between the Model and the
        /// Brain Parameters</returns>
        public IEnumerable<string> GetModelFailedChecks()
        {
            return (m_ModelParamLoader != null) ? m_ModelParamLoader.GetChecks() : new List<string>();
        }

        /// <inheritdoc />
        protected override void DecideAction()
        {
            if (m_Communicator != null)
            {
                m_Communicator?.PutObservations(name, m_Agents);
                return;
            }
            var currentBatchSize = m_Agents.Count;
            if (currentBatchSize == 0)
            {
                return;
            }

            Profiler.BeginSample("LearningBrain.DecideAction");
            if (m_Engine == null)
            {
                Debug.LogError($"No model was present for the Brain {name}.");
                return;
            }

            Profiler.BeginSample($"MLAgents.{name}.GenerateTensors");
            // Prepare the input tensors to be feed into the engine
            m_TensorGenerator.GenerateTensors(m_InferenceInputs, currentBatchSize, m_Agents);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.PrepareBarracudaInputs");
            var inputs = PrepareBarracudaInputs(m_InferenceInputs);
            Profiler.EndSample();

            // Execute the Model
            Profiler.BeginSample($"MLAgents.{name}.ExecuteGraph");
            m_Engine.Execute(inputs);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.FetchBarracudaOutputs");
            m_InferenceOutputs = FetchBarracudaOutputs(m_OutputNames);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.ApplyTensors");
            // Update the outputs
            m_TensorApplier.ApplyTensors(m_InferenceOutputs, m_Agents);
            Profiler.EndSample();

            Profiler.EndSample();
        }

        protected Dictionary<string, Tensor> PrepareBarracudaInputs(IEnumerable<TensorProxy> infInputs)
        {
            var inputs = new Dictionary<string, Tensor>();
            foreach (var inp in m_InferenceInputs)
            {
                inputs[inp.name] = inp.data;
            }

            return inputs;
        }

        protected List<TensorProxy> FetchBarracudaOutputs(string[] names)
        {
            var outputs = new List<TensorProxy>();
            foreach (var n in names)
            {
                var output = m_Engine.Peek(n);
                outputs.Add(TensorUtils.TensorProxyFromBarracuda(output, n));
            }

            return outputs;
        }

        public void OnDisable()
        {
            m_Engine?.Dispose();
            m_TensorAllocator?.Reset(false);
        }
    }
}
