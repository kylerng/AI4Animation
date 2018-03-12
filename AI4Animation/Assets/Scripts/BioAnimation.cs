﻿using UnityEngine;
using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class BioAnimation : MonoBehaviour {

	public bool Inspect = false;

	public int Framerate = 60;
	public bool ShowTrajectory = true;
	public bool ShowVelocities = true;

	public float TargetBlending = 0.25f;
	public float StyleTransition = 0.25f;
	public bool TrajectoryControl = true;
	public float TrajectoryCorrection = 1f;

	public Transform Root;
	public Transform[] Joints = new Transform[0];

	public Controller Controller;
	public Character Character;
	public MFNN MFNN;

	public bool MotionEditing = true;
	public SerialCCD[] IKSolvers = new SerialCCD[0];

	private Trajectory Trajectory;

	private Vector3 TargetDirection;
	private Vector3 TargetVelocity;
	private float Bias;

	private Vector3[] Positions = new Vector3[0];
	private Vector3[] Forwards = new Vector3[0];
	private Vector3[] Ups = new Vector3[0];
	private Vector3[] Velocities = new Vector3[0];
	
	//Trajectory for 60 Hz framerate
	private const int PointSamples = 12;
	private const int RootPointIndex = 60;
	private const int PointDensity = 10;

	private int TrajectoryDimIn;
	private int TrajectoryDimOut;
	private int JointDimIn;
	private int JointDimOut;

	void Reset() {
		Root = transform;
		Controller = new Controller();
		Character = new Character();
		Character.BuildHierarchy(transform);
		MFNN = new MFNN();
	}

	void Awake() {
		TargetDirection = new Vector3(Root.forward.x, 0f, Root.forward.z);
		TargetVelocity = Vector3.zero;
		Positions = new Vector3[Joints.Length];
		Forwards = new Vector3[Joints.Length];
		Ups = new Vector3[Joints.Length];
		Velocities = new Vector3[Joints.Length];
		Trajectory = new Trajectory(111, Controller.Styles.Length, Root.position, TargetDirection);
		Trajectory.Postprocess();
		for(int i=0; i<Joints.Length; i++) {
			Positions[i] = Joints[i].position;
			Forwards[i] = Joints[i].forward;
			Ups[i] = Joints[i].up;
			Velocities[i] = Vector3.zero;
		}
		if(MFNN.Parameters == null) {
			Debug.Log("No parameters loaded.");
			return;
		}
		MFNN.Initialise();
	}

	void Start() {
		Utility.SetFPS(60);
		JointDimIn = 12;
		JointDimOut = 12;
		TrajectoryDimIn = 7 + Controller.Styles.Length;
		TrajectoryDimOut = 4;
	}

	public Trajectory GetTrajectory() {
		return Trajectory;
	}

	public void EditMotion(bool value) {
		MotionEditing = value;
		if(MotionEditing) {
			for(int i=0; i<IKSolvers.Length; i++) {
				IKSolvers[i].TargetPosition = IKSolvers[i].EndEffector.position;
			}
		}
	}

	public void DetectJoints() {
		System.Array.Resize(ref Joints, 0);
		Action<Transform> recursion = null;
		recursion = new Action<Transform>((transform) => {
			if(Character.FindSegment(transform.name) != null) {
				System.Array.Resize(ref Joints, Joints.Length+1);
				Joints[Joints.Length-1] = transform;
			}
			for(int i=0; i<transform.childCount; i++) {
				recursion(transform.GetChild(i));
			}
		});
		recursion(Root);
	}

	void Update() {
		if(TrajectoryControl) {
			//Update Bias
			Bias = Utility.Interpolate(Bias, PoolBias(), TargetBlending);

			//Update Target Direction / Velocity 
			TargetDirection = Vector3.Lerp(TargetDirection, Quaternion.AngleAxis(Controller.QueryTurn() * 60f, Vector3.up) * Trajectory.Points[RootPointIndex].GetDirection(), TargetBlending);
			TargetVelocity = Vector3.Lerp(TargetVelocity, Bias * (Quaternion.LookRotation(TargetDirection, Vector3.up) * Controller.QueryMove()).normalized, TargetBlending);

			//Update Trajectory Correction
			TrajectoryCorrection = Utility.Interpolate(TrajectoryCorrection, Mathf.Max(Controller.QueryMove().normalized.magnitude, Mathf.Abs(Controller.QueryTurn())), TargetBlending);

			//Update Style
			for(int i=0; i<Controller.Styles.Length; i++) {
				if(i==0) {
					if(!Controller.QueryAny()) {
						Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], 1f, StyleTransition);
					} else {
						Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], Controller.Styles[i].Query() ? 1f : 0f, StyleTransition);
					}
				} else {
					Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], Controller.Styles[i].Query() ? 1f : 0f, StyleTransition);
				}
			}

			//Predict Future Trajectory
			Vector3[] trajectory_positions_blend = new Vector3[Trajectory.Points.Length];
			trajectory_positions_blend[RootPointIndex] = Trajectory.Points[RootPointIndex].GetTransformation().GetPosition();
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				float bias_pos = 0.75f;
				float bias_dir = 1.25f;
				float scale_pos = (1.0f - Mathf.Pow(1.0f - ((float)(i - RootPointIndex) / (RootPointIndex)), bias_pos));
				float scale_dir = (1.0f - Mathf.Pow(1.0f - ((float)(i - RootPointIndex) / (RootPointIndex)), bias_dir));
				
				float scale = 1f / (Trajectory.Points.Length - (RootPointIndex + 1f));

				trajectory_positions_blend[i] = trajectory_positions_blend[i-1] + Vector3.Lerp(
					Trajectory.Points[i].GetPosition() - Trajectory.Points[i-1].GetPosition(), 
					scale * TargetVelocity,
					scale_pos);

				Trajectory.Points[i].SetDirection(Vector3.Lerp(Trajectory.Points[i].GetDirection(), TargetDirection, scale_dir));

				for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
					Trajectory.Points[i].Styles[j] = Trajectory.Points[RootPointIndex].Styles[j];
				}
			}
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetPosition(trajectory_positions_blend[i]);
			}
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetVelocity((Trajectory.Points[i].GetPosition() - Trajectory.Points[i-1].GetPosition()) * Framerate);
			}
			for(int i=RootPointIndex; i<Trajectory.Points.Length; i+=PointDensity) {
				Trajectory.Points[i].Postprocess();
			}
			/*
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Point prev = GetPreviousSample(i);
				Trajectory.Point next = GetNextSample(i);
				float factor = (float)(i % PointDensity) / PointDensity;

				Trajectory.Points[i].SetPosition(((1f-factor)*prev.GetPosition() + factor*next.GetPosition()));
				Trajectory.Points[i].SetDirection(((1f-factor)*prev.GetDirection() + factor*next.GetDirection()));
				Trajectory.Points[i].SetVelocity(((1f-factor)*prev.GetVelocity() + factor*next.GetVelocity()));
				Trajectory.Points[i].SetLeftsample((1f-factor)*prev.GetLeftSample() + factor*next.GetLeftSample());
				Trajectory.Points[i].SetRightSample((1f-factor)*prev.GetRightSample() + factor*next.GetRightSample());
				Trajectory.Points[i].SetSlope((1f-factor)*prev.GetSlope() + factor*next.GetSlope());
			}
			*/
		}

		if(MFNN.Parameters != null) {
			//Calculate Root
			Matrix4x4 currentRoot = Trajectory.Points[RootPointIndex].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref currentRoot,
				new Vector3(currentRoot.GetPosition().x, 0f, currentRoot.GetPosition().z)
			);
			//

			int start = 0;
			//Input Trajectory Positions / Directions / Heights / Styles
			for(int i=0; i<PointSamples; i++) {
				//Debug.Log(start + i*TrajectoryDimIn);
				Vector3 pos = GetSample(i).GetPosition().GetRelativePositionTo(currentRoot);
				Vector3 dir = GetSample(i).GetDirection().GetRelativeDirectionTo(currentRoot);
				MFNN.SetInput(start + i*TrajectoryDimIn + 0, pos.x);
				MFNN.SetInput(start + i*TrajectoryDimIn + 1, 0f);
				MFNN.SetInput(start + i*TrajectoryDimIn + 2, pos.z);
				MFNN.SetInput(start + i*TrajectoryDimIn + 3, dir.x);
				MFNN.SetInput(start + i*TrajectoryDimIn + 4, dir.z);
				MFNN.SetInput(start + i*TrajectoryDimIn + 5, 0f);
				MFNN.SetInput(start + i*TrajectoryDimIn + 6, 0f);
				for(int j=0; j<GetSample(i).Styles.Length; j++) {
					MFNN.SetInput(start + i*TrajectoryDimIn + 7 + j, GetSample(i).Styles[j]);
				}
			}
			start += TrajectoryDimIn*PointSamples;

			//Input Previous Bone Positions / Velocities
			Matrix4x4 previousRoot = Trajectory.Points[RootPointIndex-1].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref previousRoot,
				new Vector3(previousRoot.GetPosition().x, 0f, previousRoot.GetPosition().z)
			);
			//
			for(int i=0; i<Joints.Length; i++) {
				//Debug.Log("Joint " + Joints[i].name + " at " + (start + i*JointDimIn));
				Vector3 pos = Positions[i].GetRelativePositionTo(previousRoot);
				Vector3 forward = Forwards[i].GetRelativeDirectionTo(previousRoot);
				Vector3 up = Ups[i].GetRelativeDirectionTo(previousRoot);
				Vector3 vel = Velocities[i].GetRelativeDirectionTo(previousRoot);
				MFNN.SetInput(start + i*JointDimIn + 0, pos.x);
				MFNN.SetInput(start + i*JointDimIn + 1, pos.y);
				MFNN.SetInput(start + i*JointDimIn + 2, pos.z);
				MFNN.SetInput(start + i*JointDimIn + 3, forward.x);
				MFNN.SetInput(start + i*JointDimIn + 4, forward.y);
				MFNN.SetInput(start + i*JointDimIn + 5, forward.z);
				MFNN.SetInput(start + i*JointDimIn + 6, up.x);
				MFNN.SetInput(start + i*JointDimIn + 7, up.y);
				MFNN.SetInput(start + i*JointDimIn + 8, up.z);
				MFNN.SetInput(start + i*JointDimIn + 9, vel.x);
				MFNN.SetInput(start + i*JointDimIn + 10, vel.y);
				MFNN.SetInput(start + i*JointDimIn + 11, vel.z);
			}
			start += JointDimIn*Joints.Length;
			
			//Predict
			MFNN.Predict();

			//Update Past Trajectory
			for(int i=0; i<RootPointIndex; i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i+1].GetPosition());
				Trajectory.Points[i].SetDirection(Trajectory.Points[i+1].GetDirection());
				Trajectory.Points[i].SetVelocity(Trajectory.Points[i+1].GetVelocity());
				Trajectory.Points[i].SetLeftsample(Trajectory.Points[i+1].GetLeftSample());
				Trajectory.Points[i].SetRightSample(Trajectory.Points[i+1].GetRightSample());
				Trajectory.Points[i].SetSlope(Trajectory.Points[i+1].GetSlope());
				for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
					Trajectory.Points[i].Styles[j] = Trajectory.Points[i+1].Styles[j];
				}
			}

			//Update Current Trajectory
			int end = TrajectoryDimOut*6 + JointDimOut*Joints.Length;
			Vector3 translationalOffset = new Vector3(MFNN.GetOutput(end+0), 0f, MFNN.GetOutput(end+1));
			float angularOffset = MFNN.GetOutput(end+2);

			translationalOffset *= Utility.Exponential01(translationalOffset.magnitude / 0.001f);
			angularOffset *= Utility.Exponential01(Mathf.Abs(angularOffset) / 0.01f);
			
			Trajectory.Points[RootPointIndex].SetPosition(translationalOffset.GetRelativePositionFrom(currentRoot));
			Trajectory.Points[RootPointIndex].SetDirection(Quaternion.AngleAxis(angularOffset, Vector3.up) * Trajectory.Points[RootPointIndex].GetDirection());
			Trajectory.Points[RootPointIndex].SetVelocity(translationalOffset.GetRelativeDirectionFrom(currentRoot) * Framerate);
			Trajectory.Points[RootPointIndex].Postprocess();
			Matrix4x4 nextRoot = Trajectory.Points[RootPointIndex].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref nextRoot,
				new Vector3(nextRoot.GetPosition().x, 0f, nextRoot.GetPosition().z)
			);
			//

			//Update Future Trajectory
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i].GetPosition() + translationalOffset.GetRelativeDirectionFrom(nextRoot));
			}
			start = 0;
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				//ROOT	1		2		3		4		5
				//.x....x.......x.......x.......x.......x
				int index = i;
				int prevSampleIndex = GetPreviousSample(index).GetIndex() / PointDensity;
				int nextSampleIndex = GetNextSample(index).GetIndex() / PointDensity;
				float factor = (float)(i % PointDensity) / PointDensity;

				float prevPosX = MFNN.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 0);
				float prevPosZ = MFNN.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 1);
				float prevDirX = MFNN.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 2);
				float prevDirZ = MFNN.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 3);

				float nextPosX = MFNN.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 0);
				float nextPosZ = MFNN.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 1);
				float nextDirX = MFNN.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 2);
				float nextDirZ = MFNN.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 3);

				float posX = (1f - factor) * prevPosX + factor * nextPosX;
				float posZ = (1f - factor) * prevPosZ + factor * nextPosZ;
				float dirX = (1f - factor) * prevDirX + factor * nextDirX;
				float dirZ = (1f - factor) * prevDirZ + factor * nextDirZ;

				Trajectory.Points[i].SetPosition(
					Utility.Interpolate(
						Trajectory.Points[i].GetPosition(),
						new Vector3(posX, 0f, posZ).GetRelativePositionFrom(nextRoot),
						TrajectoryCorrection
						)
					);
				Trajectory.Points[i].SetDirection(
					Utility.Interpolate(
						Trajectory.Points[i].GetDirection(),
						new Vector3(dirX, 0f, dirZ).normalized.GetRelativeDirectionFrom(nextRoot),
						TrajectoryCorrection
						)
					);
			}
			start += TrajectoryDimOut*6;
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetVelocity((Trajectory.Points[i].GetPosition() - Trajectory.Points[i-1].GetPosition()) * Framerate);
			}
			for(int i=RootPointIndex+PointDensity; i<Trajectory.Points.Length; i+=PointDensity) {
				Trajectory.Points[i].Postprocess();
			}
			/*
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Point prev = GetPreviousSample(i);
				Trajectory.Point next = GetNextSample(i);
				float factor = (float)(i % PointDensity) / PointDensity;

				Trajectory.Points[i].SetPosition(((1f-factor)*prev.GetPosition() + factor*next.GetPosition()));
				Trajectory.Points[i].SetDirection(((1f-factor)*prev.GetDirection() + factor*next.GetDirection()));
				Trajectory.Points[i].SetVelocity(((1f-factor)*prev.GetVelocity() + factor*next.GetVelocity()));
				Trajectory.Points[i].SetLeftsample((1f-factor)*prev.GetLeftSample() + factor*next.GetLeftSample());
				Trajectory.Points[i].SetRightSample((1f-factor)*prev.GetRightSample() + factor*next.GetRightSample());
				Trajectory.Points[i].SetSlope((1f-factor)*prev.GetSlope() + factor*next.GetSlope());
			}
			*/

			//Compute Posture
			for(int i=0; i<Joints.Length; i++) {
				Vector3 position = new Vector3(MFNN.GetOutput(start + i*JointDimOut + 0), MFNN.GetOutput(start + i*JointDimOut + 1), MFNN.GetOutput(start + i*JointDimOut + 2));
				Vector3 forward = new Vector3(MFNN.GetOutput(start + i*JointDimOut + 3), MFNN.GetOutput(start + i*JointDimOut + 4), MFNN.GetOutput(start + i*JointDimOut + 5)).normalized;
				Vector3 up = new Vector3(MFNN.GetOutput(start + i*JointDimOut + 6), MFNN.GetOutput(start + i*JointDimOut + 7), MFNN.GetOutput(start + i*JointDimOut + 8)).normalized;
				Vector3 velocity = new Vector3(MFNN.GetOutput(start + i*JointDimOut + 9), MFNN.GetOutput(start + i*JointDimOut + 10), MFNN.GetOutput(start + i*JointDimOut + 11));
				
				Positions[i] = Vector3.Lerp(Positions[i].GetRelativePositionTo(currentRoot) + velocity / Framerate, position, 0.5f).GetRelativePositionFrom(currentRoot);
				Forwards[i] = forward.GetRelativeDirectionFrom(currentRoot);
				Ups[i] = up.GetRelativeDirectionFrom(currentRoot);
				Velocities[i] = velocity.GetRelativeDirectionFrom(currentRoot);
			}
			start += JointDimOut*Joints.Length;
			
			//Update Posture
			Root.position = nextRoot.GetPosition();
			Root.rotation = nextRoot.GetRotation();
			for(int i=0; i<Joints.Length; i++) {
				Joints[i].position = Positions[i];
				Joints[i].rotation = Quaternion.LookRotation(Forwards[i], Ups[i]);
			}
		
			transform.position = new Vector3(Root.position.x, 0f, Root.position.z); //Fix for flat ground

			if(MotionEditing) {
				//Step #1
				for(int i=0; i<IKSolvers.Length; i++) {
					if(IKSolvers[i].name != "Tail") {
						float heightThreshold = 0.025f;
						float velocityThreshold = 0.025f;
						Vector3 target = IKSolvers[i].EndEffector.position;
						IKSolvers[i].TargetPosition.y = target.y;
						float velocityDelta = (target - IKSolvers[i].TargetPosition).magnitude;
						float velocityWeight = Utility.Exponential01(velocityDelta / velocityThreshold);
						float heightDelta = target.y;
						float heightWeight = Utility.Exponential01(heightDelta / heightThreshold);
						float weight = Mathf.Max(velocityWeight, heightWeight);
						IKSolvers[i].TargetPosition = Vector3.Lerp(IKSolvers[i].TargetPosition, target, weight);
					}
				}
				for(int i=0; i<IKSolvers.Length; i++) {
					if(IKSolvers[i].name != "Tail") {
						IKSolvers[i].Solve();
					}
				}
				for(int i=0; i<Joints.Length; i++) {
					Positions[i] = Joints[i].position;
				}
			}

			transform.position = Trajectory.Points[RootPointIndex].GetPosition();
			
			if(MotionEditing) {
				//Step #2
				for(int i=0; i<IKSolvers.Length; i++) {
					IKSolvers[i].TargetPosition = IKSolvers[i].EndEffector.position;
					float height = Utility.GetHeight(IKSolvers[i].TargetPosition, LayerMask.GetMask("Ground"));
					if(IKSolvers[i].name == "Tail") {
						IKSolvers[i].TargetPosition.y = Mathf.Max(height, height + (IKSolvers[i].TargetPosition.y - transform.position.y));
					} else {
						IKSolvers[i].TargetPosition.y = height + (IKSolvers[i].TargetPosition.y - transform.position.y);
					}
				}
				Transform spine = Array.Find(Joints, x => x.name == "Spine1");
				Transform neck = Array.Find(Joints, x => x.name == "Neck");
				Transform leftShoulder = Array.Find(Joints, x => x.name == "LeftShoulder");
				Transform rightShoulder = Array.Find(Joints, x => x.name == "RightShoulder");
				Vector3 spinePosition = spine.position;
				Vector3 neckPosition = neck.position;
				Vector3 leftShoulderPosition = leftShoulder.position;
				Vector3 rightShoulderPosition = rightShoulder.position;
				float spineHeight = Utility.GetHeight(spine.position, LayerMask.GetMask("Ground"));
				float neckHeight = Utility.GetHeight(neck.position, LayerMask.GetMask("Ground"));
				float leftShoulderHeight = Utility.GetHeight(leftShoulder.position, LayerMask.GetMask("Ground"));
				float rightShoulderHeight = Utility.GetHeight(rightShoulder.position, LayerMask.GetMask("Ground"));
				spine.rotation = Quaternion.Slerp(spine.rotation, Quaternion.FromToRotation(neckPosition - spinePosition, new Vector3(neckPosition.x, neckHeight + (neckPosition.y - Root.position.y), neckPosition.z) - spinePosition) * spine.rotation, 0.5f);
				spine.position = new Vector3(spinePosition.x, spineHeight + (spinePosition.y - Root.position.y), spinePosition.z);
				neck.position = new Vector3(neckPosition.x, neckHeight + (neckPosition.y - Root.position.y), neckPosition.z);
				leftShoulder.position = new Vector3(leftShoulderPosition.x, leftShoulderHeight + (leftShoulderPosition.y - Root.position.y), leftShoulderPosition.z);
				rightShoulder.position = new Vector3(rightShoulderPosition.x, rightShoulderHeight + (rightShoulderPosition.y - Root.position.y), rightShoulderPosition.z);
				for(int i=0; i<IKSolvers.Length; i++) {
					IKSolvers[i].Solve();
				}
			}
			
			//Update Skeleton
			Character.FetchTransformations(Root);			
		}
	}

	private float PoolBias() {
		float[] styles = Trajectory.Points[RootPointIndex].Styles;
		float bias = 0f;
		for(int i=0; i<styles.Length; i++) {
			float _bias = Controller.Styles[i].Bias;
			float max = 0f;
			for(int j=0; j<Controller.Styles[i].Multipliers.Length; j++) {
				if(Input.GetKey(Controller.Styles[i].Multipliers[j].Key)) {
					max = Mathf.Max(max, Controller.Styles[i].Bias * Controller.Styles[i].Multipliers[j].Value);
				}
			}
			for(int j=0; j<Controller.Styles[i].Multipliers.Length; j++) {
				if(Input.GetKey(Controller.Styles[i].Multipliers[j].Key)) {
					_bias = Mathf.Min(max, _bias * Controller.Styles[i].Multipliers[j].Value);
				}
			}
			bias += styles[i] * _bias;
		}
		return bias;
	}

	private Trajectory.Point GetSample(int index) {
		return Trajectory.Points[Mathf.Clamp(index*10, 0, Trajectory.Points.Length-1)];
	}

	private Trajectory.Point GetPreviousSample(int index) {
		return GetSample(index / 10);
	}

	private Trajectory.Point GetNextSample(int index) {
		if(index % 10 == 0) {
			return GetSample(index / 10);
		} else {
			return GetSample(index / 10 + 1);
		}
	}

	void OnGUI() {
		GUI.color = UltiDraw.Mustard;
		GUI.backgroundColor = UltiDraw.Black;
		float height = 0.05f;
		GUI.Box(Utility.GetGUIRect(0.025f, 0.05f, 0.3f, Controller.Styles.Length*height), "");
		for(int i=0; i<Controller.Styles.Length; i++) {
			GUI.Label(Utility.GetGUIRect(0.05f, 0.075f + i*0.05f, 0.025f, height), Controller.Styles[i].Name);
			string keys = string.Empty;
			for(int j=0; j<Controller.Styles[i].Keys.Length; j++) {
				keys += Controller.Styles[i].Keys[j].ToString() + " ";
			}
			GUI.Label(Utility.GetGUIRect(0.075f, 0.075f + i*0.05f, 0.05f, height), keys);
			GUI.HorizontalSlider(Utility.GetGUIRect(0.125f, 0.075f + i*0.05f, 0.15f, height), Trajectory.Points[RootPointIndex].Styles[i], 0f, 1f);
		}
	}

	void OnRenderObject() {
		if(Root == null) {
			Root = transform;
		}

		if(ShowTrajectory) {
			if(Application.isPlaying) {
				//UltiDraw.Begin();
				//UltiDraw.DrawLine(Trajectory.Points[RootPointIndex].GetPosition(), Trajectory.Points[RootPointIndex].GetPosition() + TargetDirection, 0.05f, 0f, UltiDraw.Orange.Transparent(0.75f));
				//UltiDraw.DrawLine(Trajectory.Points[RootPointIndex].GetPosition(), Trajectory.Points[RootPointIndex].GetPosition() + TargetVelocity, 0.05f, 0f, UltiDraw.Green.Transparent(0.75f));
				//UltiDraw.End();
				Trajectory.Draw(10);
			}
		}
		
		if(!Application.isPlaying) {
			Character.FetchTransformations(Root);
		}
		Character.Draw();

		if(ShowVelocities) {
			if(Application.isPlaying) {
				UltiDraw.Begin();
				for(int i=0; i<Joints.Length; i++) {
					Character.Segment segment = Character.FindSegment(Joints[i].name);
					if(segment != null) {
						UltiDraw.DrawArrow(
							Joints[i].position,
							Joints[i].position + Velocities[i],
							0.75f,
							0.0075f,
							0.05f,
							UltiDraw.Purple.Transparent(0.5f)
						);
					}
				}
				UltiDraw.End();
			}
		}
	}

	void OnDrawGizmos() {
		if(!Application.isPlaying) {
			OnRenderObject();
		}
	}

	#if UNITY_EDITOR
	[CustomEditor(typeof(BioAnimation))]
	public class BioAnimation_Editor : Editor {

		public BioAnimation Target;

		void Awake() {
			Target = (BioAnimation)target;
		}

		public override void OnInspectorGUI() {
			Undo.RecordObject(Target, Target.name);

			Inspector();
			Target.Controller.Inspector();
			Target.Character.Inspector(Target.Root);
			Target.MFNN.Inspector();

			if(GUI.changed) {
				EditorUtility.SetDirty(Target);
			}
		}

		private void Inspector() {			
			Utility.SetGUIColor(UltiDraw.Grey);
			using(new EditorGUILayout.VerticalScope ("Box")) {
				Utility.ResetGUIColor();

				if(Target.Character.RebuildRequired(Target.Root)) {
					EditorGUILayout.HelpBox("Rebuild required because hierarchy was changed externally.", MessageType.Error);
					if(Utility.GUIButton("Build Hierarchy", Color.grey, Color.white)) {
						Target.Character.BuildHierarchy(Target.Root);
					}
				}

				if(Utility.GUIButton("Animation", UltiDraw.DarkGrey, UltiDraw.White)) {
					Target.Inspect = !Target.Inspect;
				}

				if(Target.Inspect) {
					using(new EditorGUILayout.VerticalScope ("Box")) {
						Target.Framerate = EditorGUILayout.IntField("Framerate", Target.Framerate);
						Target.ShowTrajectory = EditorGUILayout.Toggle("Show Trajectory", Target.ShowTrajectory);
						Target.ShowVelocities = EditorGUILayout.Toggle("Show Velocities", Target.ShowVelocities);
						Target.TargetBlending = EditorGUILayout.Slider("Target Blending", Target.TargetBlending, 0f, 1f);
						Target.StyleTransition = EditorGUILayout.Slider("Style Transition", Target.StyleTransition, 0f, 1f);
						Target.TrajectoryControl = EditorGUILayout.Toggle("Trajectory Control", Target.TrajectoryControl);
						Target.TrajectoryCorrection = EditorGUILayout.Slider("Trajectory Correction", Target.TrajectoryCorrection, 0f, 1f);

						EditorGUILayout.BeginHorizontal();
						if(Utility.GUIButton("Add IK Solver", UltiDraw.Brown, UltiDraw.White)) {
							Utility.Expand(ref Target.IKSolvers);
						}
						if(Utility.GUIButton("Remove IK Solver", UltiDraw.Brown, UltiDraw.White)) {
							Utility.Shrink(ref Target.IKSolvers);
						}
						EditorGUILayout.EndHorizontal();
						Target.MotionEditing = EditorGUILayout.Toggle("Motion Editing", Target.MotionEditing);
						for(int i=0; i<Target.IKSolvers.Length; i++) {
							Target.IKSolvers[i] = (SerialCCD)EditorGUILayout.ObjectField(Target.IKSolvers[i], typeof(SerialCCD), true);
						}

						EditorGUI.BeginDisabledGroup(true);
						EditorGUILayout.ObjectField("Root", Target.Root, typeof(Transform), true);
						EditorGUI.EndDisabledGroup();
						if(Utility.GUIButton("Detect Joints", UltiDraw.DarkGrey, UltiDraw.White)) {
							Target.DetectJoints();
						}
						for(int i=0; i<Target.Joints.Length; i++) {
							if(Target.Joints[i] != null) {
								Utility.SetGUIColor(UltiDraw.Green);
							} else {
								Utility.SetGUIColor(UltiDraw.Red);
							}
							EditorGUILayout.BeginHorizontal();
							EditorGUILayout.LabelField("Joint " + (i+1), GUILayout.Width(50f));
							Target.Joints[i] = (Transform)EditorGUILayout.ObjectField(Target.Joints[i], typeof(Transform), true);
							EditorGUILayout.EndHorizontal();
							Utility.ResetGUIColor();
						}
					}
				}
			}
		}
	}
	#endif
}